﻿using System.Collections.Generic;
using System.Management.Automation;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;
using CodeOwls.PowerShell.Provider.PathNodes;
using Microsoft.PowerShell.SHiPS.Resources;


namespace Microsoft.PowerShell.SHiPS
{
    /// <summary>
    /// A class that is responsible for invoking a given powershell script. 
    /// </summary>
    internal class PSScriptRunner
    {
        /// <summary>
        /// Invokes a PowerShell script block.
        /// </summary>
        /// <param name="context">A ProviderContext object contains information that a PowerShell provider needs.</param>
        /// <param name="node">ContainerNode object that is corresponding to the current path.</param>
        /// <param name="drive">Current drive that a user is in use.</param>
        /// <param name="script">PowerShell script to be run.</param>
        /// <param name="addNodeOnly">For the cached case, the node won't get freshed.</param>
        /// <returns></returns>
        internal static IEnumerable<IPathNode> InvokeScriptBlock(
            IProviderContext context,
            SHiPSDirectory node,
            SHiPSDrive drive,
            string script,
            bool addNodeOnly = false)
        {           
            var progressId = 1;
            var activityId = Resource.RetrievingData;
            int percentComplete = 1;
            var desciption = (Resource.FetchingData).StringFormat(node.Name??"");
            int waittime = 1000; // 1s

            CancellationTokenSource cts = new CancellationTokenSource();        
            var progressTracker = new ProgressTracker(progressId, activityId, desciption, node.BuiltinProgress);

            try
            {
                ICollection<object> results = new List<object>();

                var errors = new ConcurrentBag<ErrorRecord>();
                var parameters = context.GetSHiPSParameters();
                var usingDynamicParameter = parameters.BoundParameters.UsingDynamicParameter(node, drive);
                 
                //PowerShell engine hangs if we call like this in default runspace
                //var task = Task.Factory.StartNew(() =>
                //{
                //    results = node.GetChildItem();                  

                //}, cts.Token);


                var task = Task.Factory.StartNew(() =>
                {
                    results = CallPowerShellScript(
                        node,
                        context,
                        drive.PowerShellInstance,
                        parameters,
                        script,
                        output_DataAdded,
                        (sender, e) => error_DataAdded(sender, e, errors));

                }, cts.Token);

                
                var stop = task.Wait(waittime, cts.Token);

                if (!stop && !cts.Token.IsCancellationRequested && !context.Stopping)
                {
                    progressTracker.Start(context);
                }

                while (!stop && !cts.IsCancellationRequested && !context.Stopping)
                {
                    stop = task.Wait(waittime, cts.Token);
                    progressTracker.Update(++percentComplete, context);
                }

                progressTracker.End(context);

                if (errors.Count > 0)
                {
                    // Cleanup child items only if the user type '-force'.
                    if (context.Force)
                    {
                        //remove the cached child nodes for the failed node
                        node.Children?.Clear();

                        //remove the node from its parent's children list so that it won't show again when a user types dir -force
                        node.Parent?.Children.RemoveSafe(node.Name);
                    }

                    // report the error if there are any
                    ReportErrors(node.Name, context, errors);
                    //do not yield break here as we need to display the rest of outputs
                }

                if (results == null || !results.Any())
                {
                    // Just because result is null, it does not mean that the call fails because the command may
                    // return nothing.
                    context.WriteDebug(Resource.InvokeScriptReturnNull.StringFormat(node.Name, context.Path));

                    if (errors.Count == 0)
                    {
                        // do not mark the node visited if there is an error. e.g., if login to azure fails, 
                        // we should not cache so that a user can dir again once the cred resolved.
                        node.ItemNavigated = true;
                    }

                    //clear the child node list as the current node has null or empty children (results)
                    if (context.Force) { node.Children?.Clear();}
                    return Enumerable.Empty<IPathNode>();
                }

                return (node.UseCache && !usingDynamicParameter) ? ProcessResultsWithCache(results, context, node, drive, addNodeOnly) : ProcessResultsWithNoCache(results, context, node, drive);
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                cts.Dispose();

                progressTracker.End(context);

                // We complete the call. Reset the node parameters.
                node.SHiPSProviderContext.Clear();

                //stop the running script
                drive.PowerShellInstance.Stop();
            }
        }

        private static IEnumerable<IPathNode> ProcessResultsWithCache(
            ICollection<object> results,
            IProviderContext context,
            SHiPSDirectory node,
            SHiPSDrive drive,
            bool addNodeOnly)
        {
            //TODO: async vs cache
            //we could yield result right away but we need to save the all children in the meantime because 
            //InvokeScript can be costly.So we need to cache the results first by completing the foreach before 
            //returning to a caller.

            List<IPathNode> retval = new List<IPathNode>();

            if (!addNodeOnly)
            {
                //clear the child node list, get ready to get the refreshed ones
                node.Children?.Clear();
            }

            foreach (var result in results.WhereNotNull())
            {
                // Making sure to obey the StopProcessing.
                if (context.Stopping)
                {
                    return null;
                }
                node.AddAsChildNode(result, drive, addNodeOnly, retval);
            }

            // Mark the node visited once we sucessfully fetched data.
            node.ItemNavigated = true;

            return retval;
        }

        private static IEnumerable<IPathNode> ProcessResultsWithNoCache(
            ICollection<object> results,
            IProviderContext context,
            SHiPSDirectory node,
            SHiPSDrive drive)
        {
            foreach (var result in results.WhereNotNull())
            {
                // Making sure to obey the StopProcessing.
                if (context.Stopping)
                {
                    yield break;
                }

                var cnode = node.GetChildPNode(result, drive, false, cache:false);
                if (cnode != null)
                {
                    yield return cnode;
                }
            }
        }

        /// <summary>
        /// Invokes a PowerShell script block.
        /// </summary>
        /// <param name="script">A script to be invoked.</param>
        /// <param name="powerShell">PowerShell instance.</param>
        /// <param name="errorAction">Error callback function.</param>
        /// <returns></returns>
        internal static ICollection<PSObject> CallPowerShellScript(
            string script,
            System.Management.Automation.PowerShell powerShell,
            EventHandler<DataAddedEventArgs> errorAction)
        {
            try
            {
                powerShell.Clear();

                var input = new PSDataCollection<PSObject>();
                input.Complete();

                var output = new PSDataCollection<PSObject>();
                output.DataAdded += output_DataAdded;
                
                if (errorAction != null)
                {
                    powerShell.Streams.Error.DataAdded += errorAction;
                }

                powerShell.AddScript(script);
                powerShell.Invoke(null, output, new PSInvocationSettings());

                return output.Count == 0 ? null : output;
            }
            finally
            {
                powerShell.Streams.Error.DataAdded -= errorAction;
            }
        }

        internal static ICollection<object> CallPowerShellScript(
            SHiPSBase node,
            IProviderContext context,
            System.Management.Automation.PowerShell powerShell,
            SHiPSParameters parameters,
            string script,
            EventHandler<DataAddedEventArgs> outputAction,
            EventHandler<DataAddedEventArgs> errorAction)
        {
            if (node == null)
            {
                throw new ArgumentNullException("node");
            }

            try
            {
                powerShell.Clear();

                var input = new PSDataCollection<object>();
                input.Complete();

                var output = new PSDataCollection<object>();

                if (outputAction != null)
                {
                    output.DataAdded += outputAction;
                }

                //register events
                if (errorAction != null)
                {
                    powerShell.Streams.Error.DataAdded += errorAction;
                }

                // Calling the following throws 'Unable to cast object of type 'System.Management.Automation.Language.FunctionMemberAst' to 
                // type 'System.Management.Automation.Language.FunctionDefinitionAst'.
                //output = node.GetChildItem();

                //make script block             
                powerShell.AddScript(script);
                powerShell.AddParameter("object", node);
                
                
                if (parameters != null)
                {
                    if (parameters.Debug)
                    {
                        powerShell.AddParameter("debug");
                    }
                    if (parameters.Verbose)
                    {
                        powerShell.AddParameter("verbose");
                    }
                }

                powerShell.Invoke(null, output, new PSInvocationSettings());

                return output.Count == 0 ? null : output;
            }
            finally 
            {
                powerShell.Streams.Error.DataAdded -= errorAction;
            }
        }

        internal static void error_DataAdded(object sender, DataAddedEventArgs e, ConcurrentBag<ErrorRecord> errors)
        {
            PSDataCollection<ErrorRecord> errorStream = sender as PSDataCollection<ErrorRecord>;

            if (errorStream == null)
            {
                return;
            }

            var error = errorStream[e.Index];

            if (error != null)
            {
                // add the error so we can report them later
                errors.Add(error);
            }

        }

        internal static void output_DataAdded(object sender, DataAddedEventArgs e)
        {
            PSDataCollection<PSObject> outputstream = sender as PSDataCollection<PSObject>;

            if (outputstream == null)
            {
                return;
            }

            PSObject psObject = outputstream[e.Index];
            if (psObject != null)
            {
                var value = psObject.ImmediateBaseObject;
                //TODO we can yield data here.
                //save it to cache
                //yield data
            }
        }

        private static void ReportErrors(string item, IProviderContext context, IEnumerable<ErrorRecord> errors)
        {
            foreach (var error in errors)
            {
                var msg = Resource.MaybeItemNotExist.StringFormat(item, error.ErrorDetails == null ? error.Exception.Message : error.ErrorDetails.Message);
                context.ReportError(error.FullyQualifiedErrorId, msg, error.CategoryInfo.Category, "SHiPS");

                if (!string.IsNullOrWhiteSpace(error.Exception.StackTrace))
                {
                    // give a debug hint if we have a script stack trace for more info
                    context.WriteDebug(error.Exception.StackTrace);
                }
            }
        }
    }
}

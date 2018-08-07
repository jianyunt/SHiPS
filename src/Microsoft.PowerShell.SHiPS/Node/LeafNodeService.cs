using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using CodeOwls.PowerShell.Paths;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;
using CodeOwls.PowerShell.Provider.PathNodes;

namespace Microsoft.PowerShell.SHiPS
{
    /// <summary>
    /// Defines actions that applies to a SHiPSLeaf node.
    /// </summary>
    internal class LeafNodeService : PathNodeBase, IGetItemContent, IClearItemContent
    {
        private readonly SHiPSLeaf _shipsLeaf;
        private static readonly string _leaf = ".";
        private readonly SHiPSDrive _drive;

        internal LeafNodeService(object leafObject, SHiPSDrive drive)
        {
            _shipsLeaf = leafObject as SHiPSLeaf;
            _drive = drive;
        }

        public override IPathValue GetNodeValue()
        {
            return new LeafPathValue(_shipsLeaf, Name);
        }

        public override string ItemMode
        {
            get {return _leaf; }
        }

        public override string Name
        {
            get { return _shipsLeaf.Name; }
        }

        #region IGetItemContent

        public IContentReader GetContentReader(IProviderContext context)
        {
            var errors = new ConcurrentBag<ErrorRecord>();

            var script = "[CmdletBinding()] param([object]$object)  $object.{0}()".StringFormat(Constants.GetContent);

            var results = PSScriptRunner.CallPowerShellScript(
             _shipsLeaf,
             context,
             _drive.PowerShellInstance,
             null,
             script,
             PSScriptRunner.output_DataAdded,
             (sender, e) => PSScriptRunner.error_DataAdded(sender, e, errors));


            if (errors.WhereNotNull().Any())
            {
                var error = errors.FirstOrDefault();
                if (error != null)
                {
                    var message = Environment.NewLine;
                    message += error.ErrorDetails == null ? error.Exception.Message : error.ErrorDetails.Message;
                    _drive.SHiPS.WriteWarning(message);
                }
            }

            var file = results?.FirstOrDefault();
            if (file != null)
            {
                var path = file.ToString();
                if (File.Exists(path))
                {
                    var stream = new ContentReaderWriter(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, context, _drive, _shipsLeaf);
                    return stream;
                }
            }

            return null;
        }

        public object GetContentReaderDynamicParameters(IProviderContext context)
        {
            return null;
        }

        #endregion

        #region IClearItemContent

        public void ClearContent(IProviderContext providerContext)
        {
            // Define ClearContent for now as the PowerShell engine calls ClearContent first for Set-Content cmdlet.
            return;
        }

        public object ClearContentDynamicParameters(IProviderContext providerContext)
        {
            return null;
        }

        #endregion
    }
}

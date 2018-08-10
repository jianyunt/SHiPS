using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation.Provider;
using CodeOwls.PowerShell.Paths;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;
using CodeOwls.PowerShell.Provider.PathNodes;

namespace Microsoft.PowerShell.SHiPS
{
    /// <summary>
    /// Defines actions that applies to a SHiPSLeaf node.
    /// </summary>
    internal class LeafNodeService : PathNodeBase, IGetItemContent, IClearItemContent, IInvokeItem
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
            var script = Constants.ScriptBlockWithParam1.StringFormat(Constants.GetContent);
            var results = PSScriptRunner.InvokeScriptBlock(_shipsLeaf, _drive, script);

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

        public object InvokeItemParameters
        {
            get
            {
                var script = Constants.ScriptBlockWithParam1.StringFormat(Constants.InvokeItemDynamicParameters);
                var parameters = PSScriptRunner.InvokeScriptBlock(_shipsLeaf, _drive, script);
                return parameters?.FirstOrDefault(); ;
            } 
        }
        public IEnumerable<object> InvokeItem(IProviderContext context, string path)
        {
            // Set the DynamicParameters before calling InvokeItem method written in PS script
            _shipsLeaf.SHiPSProviderContext.DynamicParameters = context.DynamicParameters;

            // Calling SHiPS based PowerShell provider 'void InvokeItem([string]$path)'
            var script = Constants.ScriptBlockWithParams2.StringFormat(Constants.InvokeItem, path);
            PSScriptRunner.InvokeScriptBlock(_shipsLeaf, _drive, script);

            return null;
        }
    }
}

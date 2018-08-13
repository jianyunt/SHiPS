using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation.Provider;
using System.Management.Automation;
using CodeOwls.PowerShell.Provider;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;

namespace Microsoft.PowerShell.SHiPS
{
    internal enum AccessMode
    {
        Set = 1,
        Get = 2
    }

    /// <summary>
    /// The content stream class for the SHiPS provider. It implements both
    /// the IContentReader and IContentWriter interfaces.
    /// </summary>
    internal class ContentReaderWriter : IContentReader, IContentWriter
    {
        private readonly string _tempFilePath;
        private readonly CmdletProvider _provider;
        private FileStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly SHiPSBase _node;
        private readonly SHiPSDrive _drive;
        private readonly IProviderContext _context;
        private readonly AccessMode _mode;

        /// <summary>
        /// Constructor for the content stream.
        /// </summary>
        /// <param name="mode">The file mode to open the file with.</param>
        /// <param name="objects">The file access requested in the file.</param>
        /// <param name="context"></param>
        /// <param name="drive"></param>
        /// <param name="node"></param>
        public ContentReaderWriter(ICollection<object> objects,  AccessMode mode, IProviderContext context, SHiPSDrive drive, SHiPSBase node)
        {
            _provider = context.CmdletProvider;
            _drive = drive;
            _node = node;
            _context = context;
            _tempFilePath = Path.GetTempFileName();
            CreateStreams(objects, mode);
            _mode = mode;
        }

        /// <summary>
        /// Reads the specified number of characters or a lines from the file.
        /// </summary>
        ///
        /// <param name="readCount">
        /// If less than 1, then the entire file is read at once. If 1 or greater, then
        /// readCount is used to determine how many items (ie: lines, bytes, delimited tokens)
        /// to read per call.
        /// </param>
        ///
        /// <returns>
        /// An array of strings representing the character(s) or line(s) read from
        /// the file.
        /// </returns>
        public IList Read(long readCount)
        {
            var blocks = new ArrayList();
            var readToEnd = readCount <= 0;

            try
            {
                for (var currentBlock = 0; (currentBlock < readCount) || (readToEnd); ++currentBlock)
                {
                    if (_provider.Stopping)
                    {
                        break;
                    }

                    if (!ReadByLine(blocks))
                    {
                        // EOF
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                if ((e is IOException) ||
                    (e is ArgumentException) ||
                    (e is System.Security.SecurityException) ||
                    (e is UnauthorizedAccessException))
                {
                    //Exception contains specific message about the error occured and so no need for errordetails.
                    _provider.WriteError(new ErrorRecord(e, "GetContentReaderIOError", ErrorCategory.ReadError, _tempFilePath));
                    return null;
                }

                throw;
            }

            return blocks.ToArray();
        }

        private bool ReadByLine(ArrayList blocks)
        {
            // Reading lines as strings
            var line = _reader.ReadLine();

            if (line != null)
            {
                blocks.Add(line);
            }

            var peekResult = _reader.Peek();
            return peekResult != -1;
        }

        private void CreateStreams(ICollection<object> objects, AccessMode accessMode)
        {
            _stream = new FileStream(_tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (accessMode == AccessMode.Get && objects != null && objects.Any())
            {
                _writer = new StreamWriter(_stream);
                foreach (var obj in objects)
                {
                    _writer.WriteLine(obj.ToArgString());
                }
                _writer.Flush();

            }

            // Set to beginning of the stream.
            _stream.Seek(0, SeekOrigin.Begin);

            // Open the reader stream
            if (accessMode == AccessMode.Get)
            {
                _reader = new StreamReader(_stream);
            }
            else
            {
                _writer = new StreamWriter(_stream);
            }
        }

        /// <summary>
        /// Moves the current stream position in the file
        /// </summary>
        ///
        /// <param name="offset"> The offset from the origin to move the position to. </param>
        /// <param name="origin"> The origin from which the offset is calculated. </param>
        public void Seek(long offset, SeekOrigin origin)
        {
            _writer?.Flush();
            _stream.Seek(offset, origin);
            _writer?.Flush();
            _reader?.DiscardBufferedData();
        }

        /// <summary>
        /// Closes the file.
        /// </summary>
        public void Close()
        {
            var streamClosed = false;
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                finally
                {
                    streamClosed = true;
                }
            }

            if (_reader != null)
            {
                _reader.Dispose();
                streamClosed = true;
            }

            if (!streamClosed)
            {
                _stream.Flush();
                _stream.Dispose();
            }

            // Calling the PowerShell module

            if (AccessMode.Set == _mode)
            {
                var script = Constants.ScriptBlockWithParams3.StringFormat(Constants.SetContent, _tempFilePath, _context.Path);

                // Invoke the SetContent and update cached item if applicable
                PSScriptRunner.InvokeScriptBlockAndBuildTree(_context, _node as SHiPSDirectory, _drive, script, PSScriptRunner.SetContentNotSupported, addNodeOnly: true);
            }

            //clean up
            File.Delete(_tempFilePath);
        }

        /// <summary>
        /// Writes the specified object to the file
        /// </summary>
        /// <param name="content"> The objects to write to the file </param>
        /// <returns> The objects written to the file. </returns>
        public IList Write(IList content)
        {
            foreach (var line in content)
            {
                var contentArray = line as object[];
                if (contentArray != null)
                {
                    foreach (var obj in contentArray)
                    {
                        WriteObject(obj);
                    }
                }
                else
                {
                    WriteObject(line);
                }
            }
            return content;
        }

        private void WriteObject(object content)
        {
            if (content != null)
            {
                _writer.WriteLine(content.ToString());
            }
        }

        /// <summary>
        /// Closes the file stream
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal void Dispose(bool isDisposing)
        {
            if (!isDisposing) { return; }

            _stream?.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
        }
    }
}

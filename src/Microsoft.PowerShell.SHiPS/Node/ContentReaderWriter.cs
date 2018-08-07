using System;
using System.Collections;
using System.IO;
using System.Management.Automation.Provider;
using System.Management.Automation;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;

namespace Microsoft.PowerShell.SHiPS
{

    /// <summary>
    /// The content stream class for the SHiPS provider. It implements both
    /// the IContentReader and IContentWriter interfaces.
    /// </summary>
    internal class ContentReaderWriter : IContentReader, IContentWriter
    {
        private readonly string _path;
        private readonly CmdletProvider _provider;
        private FileStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly SHiPSBase _node;
        private readonly SHiPSDrive _drive;
        private readonly IProviderContext _context;

        /// <summary>
        /// Constructor for the content stream.
        /// </summary>
        /// <param name="path">The path to the file to get the content from.</param>
        /// <param name="mode">The file mode to open the file with.</param>
        /// <param name="access">The file access requested in the file.</param>
        /// <param name="share">The file share to open the file with.</param>
        /// <param name="context"></param>
        /// <param name="drive"></param>
        /// <param name="node"></param>
        public ContentReaderWriter(string path, FileMode mode, FileAccess access, FileShare share, IProviderContext context, SHiPSDrive drive, SHiPSBase node)   
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            _path = path;
            _provider = context.CmdletProvider;
            _drive = drive;
            _node = node;
            _context = context;
            CreateStreams(path, mode, access, share);
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
                    _provider.WriteError(new ErrorRecord(e, "GetContentReaderIOError", ErrorCategory.ReadError, _path));
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

        private void CreateStreams(string filePath, FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
        {
            _stream = new FileStream(filePath, fileMode, fileAccess, fileShare);

            // Open the reader stream
            if ((fileAccess & (FileAccess.Read)) != 0)
            {
                _reader = new StreamReader(_stream);
            }

            // Open the writer stream
            if ((fileAccess & (FileAccess.Write)) != 0)
            {
                // Set to beginning of the stream.
                _stream.Seek(0, SeekOrigin.Begin);
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
            var setcontent = _stream.CanWrite;
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

            if (setcontent)
            {
                var script="[CmdletBinding()] param([object]$object)  $object.{0}('{1}', '{2}')".StringFormat(Constants.SetContent, _path, _context.Path);

                // Invoke the SetContent and update cached item if applicable
                PSScriptRunner.InvokeScriptBlock(_context, _node as SHiPSDirectory, _drive, script, addNodeOnly:true);
            }
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
            if (!isDisposing) { return;}

            _stream?.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
        }
    }
}

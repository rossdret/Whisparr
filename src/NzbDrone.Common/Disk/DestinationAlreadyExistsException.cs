using System;
using System.IO;

namespace NzbDrone.Common.Disk
{
    public class DestinationAlreadyExistsException : IOException
    {
        public string DestinationPath { get; }

        public DestinationAlreadyExistsException()
        {
        }

        public DestinationAlreadyExistsException(string message)
            : base(message)
        {
        }

        public DestinationAlreadyExistsException(string message, int hresult)
            : base(message, hresult)
        {
        }

        public DestinationAlreadyExistsException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public DestinationAlreadyExistsException(string message, string destinationPath, Exception innerException)
            : base(message, innerException)
        {
            DestinationPath = destinationPath;
        }
    }
}

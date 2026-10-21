using System;

namespace Argus.Simulation.Host
{
    // A run configuration file that cannot be used; the message names the field.
    public sealed class RunConfigurationException : Exception
    {
        public RunConfigurationException(string message)
            : base(message)
        {
        }
    }
}

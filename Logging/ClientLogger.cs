using Serilog;
using System;

namespace Opc.Ua.Edge.Translator.Logging
{
    /// <summary>
    /// Helper class for client-specific logging with color coding
    /// </summary>
    public static class ClientLogger
    {
        /// <summary>
        /// Creates a logger with client type context for color-coded output
        /// </summary>
        /// <param name="clientType">The type of client (Modbus, CIP, S7, ADS, etc.)</param>
        /// <returns>A logger enriched with client type context</returns>
        public static ILogger ForClient(string clientType)
        {
            return Log.ForContext("ClientType", clientType);
        }

        /// <summary>
        /// Gets ANSI color code for a client type
        /// </summary>
        public static string GetClientColor(string clientType)
        {
            return clientType?.ToUpperInvariant() switch
            {
                "MODBUS" => "\x1b[36m",      // Cyan
                "CIP" or "ROCKWELL" or "EIP" => "\x1b[33m",  // Yellow
                "S7" or "SIEMENS" => "\x1b[32m",  // Green
                "ADS" or "BECKHOFF" => "\x1b[35m",  // Magenta
                "OPCUA" or "UA" => "\x1b[34m",  // Blue
                "BACNET" => "\x1b[31m",  // Red
                "MITSUBISHI" => "\x1b[93m",  // Bright Yellow
                "IEC61850" => "\x1b[96m",  // Bright Cyan
                _ => "\x1b[37m"  // White (default)
            };
        }

        /// <summary>
        /// ANSI reset code
        /// </summary>
        public const string Reset = "\x1b[0m";
    }
}


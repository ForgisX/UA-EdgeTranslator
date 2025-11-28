using Serilog.Events;
using Serilog.Formatting;
using System;
using System.IO;

namespace Opc.Ua.Edge.Translator
{
    /// <summary>
    /// Custom formatter that adds color codes to client types in console output
    /// </summary>
    public class ClientTypeColorFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            // Get client type from log event properties
            string clientType = "SYSTEM";
            string colorCode = "\x1b[37m"; // White default
            string resetCode = "\x1b[0m";

            if (logEvent.Properties.TryGetValue("ClientType", out var clientTypeProperty))
            {
                clientType = clientTypeProperty.ToString().Trim('"');
                colorCode = GetClientColor(clientType);
            }

            // Format timestamp
            var timestamp = logEvent.Timestamp.ToString("HH:mm:ss");

            // Get level color and text
            var levelColor = GetLevelColor(logEvent.Level);
            var levelText = logEvent.Level.ToString().ToUpperInvariant().PadRight(3).Substring(0, 3);
            var levelReset = "\x1b[0m";

            // Write formatted output with colors: [HH:mm:ss LEVEL] CLIENTTYPE message
            output.Write($"[{timestamp} {levelColor}{levelText}{levelReset}] {colorCode}{clientType.PadRight(10)}{resetCode} ");

            // Write message
            logEvent.RenderMessage(output);
            
            // Write exception if present
            if (logEvent.Exception != null)
            {
                output.Write("\n");
                output.Write(logEvent.Exception);
            }

            output.Write("\n");
        }

        private string GetClientColor(string clientType)
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

        private string GetLevelColor(LogEventLevel level)
        {
            return level switch
            {
                LogEventLevel.Verbose => "\x1b[90m",  // Dark Gray
                LogEventLevel.Debug => "\x1b[37m",   // White
                LogEventLevel.Information => "\x1b[36m",  // Cyan
                LogEventLevel.Warning => "\x1b[33m",  // Yellow
                LogEventLevel.Error => "\x1b[31m",   // Red
                LogEventLevel.Fatal => "\x1b[35m",   // Magenta
                _ => "\x1b[0m"  // Reset
            };
        }
    }
}


# Color-Coded Logging for Protocol Clients

This logging system provides color-coded console output to help visually distinguish log messages from different protocol clients.

## Color Scheme

Each client type has a unique color:

- **Modbus** - Cyan (`\x1b[36m`)
- **CIP/Rockwell/EIP** - Yellow (`\x1b[33m`)
- **S7/Siemens** - Green (`\x1b[32m`)
- **ADS/Beckhoff** - Magenta (`\x1b[35m`)
- **OPC UA/UA** - Blue (`\x1b[34m`)
- **BACnet** - Red (`\x1b[31m`)
- **Mitsubishi** - Bright Yellow (`\x1b[93m`)
- **IEC61850** - Bright Cyan (`\x1b[96m`)
- **System/Default** - White (`\x1b[37m`)

## Usage

### In Protocol Clients

To use color-coded logging in a protocol client, add a logger field and use it instead of `Log.Logger`:

```csharp
using Opc.Ua.Edge.Translator.Logging;
using Serilog;

public class MyClient : IAsset
{
    private readonly ILogger _logger = ClientLogger.ForClient("MyClientType");
    
    public void SomeMethod()
    {
        _logger.Information("This message will be color-coded!");
        _logger.Debug("Debug messages also get colored");
        _logger.Error("Error messages too");
    }
}
```

### Client Types

Use these client type names (case-insensitive):
- `"Modbus"` for ModbusTCPClient
- `"CIP"` or `"Rockwell"` or `"EIP"` for RockwellClient
- `"S7"` or `"Siemens"` for SiemensClient
- `"ADS"` or `"Beckhoff"` for BeckhoffClient
- `"OPCUA"` or `"UA"` for UAClient
- `"BACnet"` for BACNetClient
- `"Mitsubishi"` for MitsubishiClient
- `"IEC61850"` for IEC61850Client

## Output Format

The console output format is:
```
[HH:mm:ss LEVEL] CLIENTTYPE message
```

Where:
- `HH:mm:ss` - Timestamp
- `LEVEL` - Log level (colored by level: Info=Cyan, Warning=Yellow, Error=Red, etc.)
- `CLIENTTYPE` - Client type (colored by client type, 10 characters wide)
- `message` - The actual log message

## Implementation Details

1. **ClientLogger.cs** - Helper class that creates loggers with client type context
2. **ClientTypeColorFormatter.cs** - Custom formatter that applies ANSI color codes based on the `ClientType` property in the log event
3. **Program.cs** - Updated to use the custom formatter for console output

## Adding New Client Types

To add a new client type:

1. Add the color mapping in `ClientTypeColorFormatter.GetClientColor()`:
```csharp
"MYCLIENT" => "\x1b[91m",  // Bright Red (example)
```

2. Use it in your client:
```csharp
private readonly ILogger _logger = ClientLogger.ForClient("MyClient");
```

## Notes

- Colors only apply to console output (stdout)
- File logs remain uncolored (plain text)
- Colors use ANSI escape codes, which work in most modern terminals
- If your terminal doesn't support colors, the escape codes will be visible but harmless



namespace Opc.Ua.Edge.Translator.ProtocolDrivers
{
    using Opc.Ua.Edge.Translator;
    using Opc.Ua.Edge.Translator.Interfaces;
    using Opc.Ua.Edge.Translator.Models;
    using Opc.Ua.Edge.Translator.Logging;
    using Serilog;
    using Sharp7;
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;

    public class SiemensClient : IAsset
    {
        private readonly ILogger _logger = ClientLogger.ForClient("S7");
        private S7Client _S7 = null;

        private string _endpoint = string.Empty;

        public List<string> Discover()
        {
            // S7Comm does not support discovery
            return new List<string>();
        }

        public ThingDescription BrowseAndGenerateTD(string name, string endpoint)
        {
            ThingDescription td = new()
            {
                Context = new string[1] { "https://www.w3.org/2022/wot/td/v1.1" },
                Id = "urn:" + name,
                SecurityDefinitions = new() { NosecSc = new NosecSc() { Scheme = "nosec" } },
                Security = new string[1] { "nosec_sc" },
                Type = new string[1] { "Thing" },
                Name = name,
                Base = endpoint,
                Title = name,
                Properties = new Dictionary<string, Property>(),
                Actions = new Dictionary<string, TDAction>()
            };

            var endpointParts = endpoint.Split(':');
            Connect(endpointParts[1], int.Parse(endpointParts[2]));

            // read the first 100 blocks until an error is encountered
            for (var i = 0; i < 100; i++)
            {
                var sizeRead = 0;
                var buffer = new byte[65536]; // Maximum size for a DB
                try
                {
                    var dbResult = _S7.DBGet(1, buffer, ref sizeRead);
                    if (dbResult != 0)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.Message, ex);
                    break;
                }

                var propertyName = "DB" + i.ToString() + "?0";

                S7Form form = new()
                {
                    Href = propertyName,
                    Op = new Op[2] { Op.Readproperty, Op.Observeproperty },
                    PollingTime = 1000,
                    S7DBNumber = i,
                    S7Start = 0,
                    S7Size = sizeRead,
                    Type = TypeString.String,
                };

                _logger.Information("S7 DB" + i.ToString() + ": " + BitConverter.ToString(buffer, 0, sizeRead));

                Property property = new()
                {
                    Type = TypeEnum.String,
                    ReadOnly = true,
                    Observable = true,
                    Forms = new object[1] { form }
                };

                if (!td.Properties.ContainsKey(propertyName))
                {
                    td.Properties.Add(propertyName, property);
                }
            }

            return td;
        }

        public void Connect(string ipAddress, int port)
        {
            try
            {
                _endpoint = ipAddress;

                _S7 = new();

                // assume rack 0
                var result = _S7.ConnectTo(ipAddress, 0, port);

                if (result == 0)
                {
                    _logger.Information($"Connected to Siemens S7 at {ipAddress}:{port}");
                }
                else
                {
                    _logger.Error($"Failed to connect to Siemens S7 at {ipAddress}:{port}, error code: {result}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception connecting to Siemens S7 at {ipAddress}:{port}: {ex.Message}", ex);
            }
        }

        public void Disconnect()
        {
            if (_S7 != null)
            {
                _S7.Disconnect();
                _S7 = null;
            }
        }

        public string GetRemoteEndpoint()
        {
            return _endpoint;
        }

        public object Read(AssetTag tag)
        {
            // Parse S7 address format: "DB{number}?{byteOffset}" or "DB{number}"
            // Examples: "DB1?0", "DB1?4", "DB2?10"
            int dbNumber = 1;
            int byteOffset = 0;
            int byteCount = 0;

            try
            {
                string[] addressParts = tag.Address.Split(['?', '&', '=']);
                
                if (addressParts.Length >= 1)
                {
                    // Extract DB number from address like "DB1" or "DB1?0"
                    string dbPart = addressParts[0];
                    if (dbPart.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                    {
                        string dbNumberStr = dbPart.Substring(2);
                        if (int.TryParse(dbNumberStr, out int parsedDbNumber))
                        {
                            dbNumber = parsedDbNumber;
                        }
                        else
                        {
                            _logger.Error($"Failed to parse DB number from '{dbPart}', extracted '{dbNumberStr}'");
                            return null;
                        }
                    }
                    else
                    {
                        _logger.Error($"S7 address does not start with 'DB': '{tag.Address}'");
                        return null;
                    }
                    
                    // Extract byte offset if present
                    if (addressParts.Length >= 2)
                    {
                        if (int.TryParse(addressParts[1], out int parsedOffset))
                        {
                            byteOffset = parsedOffset;
                        }
                        else
                        {
                            _logger.Warning($"Failed to parse byte offset from '{addressParts[1]}', using 0");
                        }
                    }
                }

                // Determine byte count based on data type
                byteCount = GetDataTypeByteSize(tag.Type);

                _logger.Debug($"S7 Read: DB={dbNumber}, Offset={byteOffset}, Count={byteCount}, Type={tag.Type}");

                object value = null;

                if (byteCount > 0)
                {
                    byte[] tagBytes = Read(dbNumber, byteOffset, byteCount).GetAwaiter().GetResult();

                    if ((tagBytes != null) && (tagBytes.Length > 0))
                    {
                        // S7 protocol uses big-endian (network byte order)
                        // C# BitConverter uses little-endian, so we need to swap bytes for multi-byte values
                        byte[] swappedBytes = tagBytes;
                        if (tag.Type == "Float" || tag.Type == "Integer")
                        {
                            // Swap bytes for Float (4 bytes) and Integer (2 or 4 bytes)
                            swappedBytes = ByteSwapper.Swap(tagBytes, false);
                        }

                        if (tag.Type == "Float")
                        {
                            value = BitConverter.ToSingle(swappedBytes);
                        }
                        else if (tag.Type == "Boolean")
                        {
                            // For boolean, read single byte and check if non-zero
                            value = tagBytes[0] != 0;
                        }
                        else if (tag.Type == "Integer")
                        {
                            // For integer, read as Int16 (2 bytes) or Int32 (4 bytes) based on size
                            if (byteCount == 2)
                            {
                                value = BitConverter.ToInt16(swappedBytes);
                            }
                            else
                            {
                                value = BitConverter.ToInt32(swappedBytes);
                            }
                        }
                        else if (tag.Type == "String")
                        {
                            value = Encoding.UTF8.GetString(tagBytes).TrimEnd('\0');
                        }
                        else
                        {
                            throw new ArgumentException($"Type not supported by Siemens: {tag.Type}");
                        }
                    }
                }

                return value;
            }
            catch (FormatException ex)
            {
                _logger.Error($"Format exception parsing S7 address '{tag.Address}': {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error reading S7 tag '{tag.Address}': {ex.Message}", ex);
                return null;
            }
        }

        public void Write(AssetTag tag, string value)
        {
            // Parse S7 address format: "DB{number}?{byteOffset}" or "DB{number}"
            int dbNumber = 1;
            int byteOffset = 0;

            string[] addressParts = tag.Address.Split(['?', '&', '=']);
            
            if (addressParts.Length >= 1)
            {
                // Extract DB number from address like "DB1" or "DB1?0"
                string dbPart = addressParts[0];
                if (dbPart.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(dbPart.Substring(2), out int parsedDbNumber))
                    {
                        dbNumber = parsedDbNumber;
                    }
                }
                
                // Extract byte offset if present
                if (addressParts.Length >= 2)
                {
                    if (int.TryParse(addressParts[1], out int parsedOffset))
                    {
                        byteOffset = parsedOffset;
                    }
                }
            }

            byte[] tagBytes = null;

            if (tag.Type == "Float")
            {
                tagBytes = BitConverter.GetBytes(float.Parse(value));
            }
            else if (tag.Type == "Boolean")
            {
                tagBytes = new byte[] { (byte)(bool.Parse(value) ? 1 : 0) };
            }
            else if (tag.Type == "Integer")
            {
                // Determine if Int16 or Int32 based on value size
                int intValue = int.Parse(value);
                if (intValue >= short.MinValue && intValue <= short.MaxValue)
                {
                    tagBytes = BitConverter.GetBytes((short)intValue);
                }
                else
                {
                    tagBytes = BitConverter.GetBytes(intValue);
                }
            }
            else if (tag.Type == "String")
            {
                tagBytes = Encoding.UTF8.GetBytes(value);
            }
            else
            {
                throw new ArgumentException($"Type not supported by Siemens: {tag.Type}");
            }

            Write(dbNumber, byteOffset, tagBytes).GetAwaiter().GetResult();
        }


        private Task<byte[]> Read(int dbNumber, int byteOffset, int byteCount)
        {
            if (_S7 == null)
            {
                _logger.Error("S7 client is not connected");
                return Task.FromResult((byte[])null);
            }

            try
            {
                var buffer = new byte[byteCount];
                // Sharp7 DBRead signature: DBRead(int DBNumber, int Start, int Size, byte[] Buffer)
                // Parameters: DBNumber (1-based), Start (byte offset), Size (bytes to read), Buffer (output)
                _logger.Debug($"Calling S7.DBRead with: dbNumber={dbNumber} (type: {dbNumber.GetType()}), byteOffset={byteOffset}, byteCount={byteCount}");
                int result = _S7.DBRead(dbNumber, byteOffset, byteCount, buffer);
                if (result != 0)
                {
                    _logger.Error($"S7 DBRead failed for DB{dbNumber} at offset {byteOffset}, size {byteCount}: error code {result}");
                    return Task.FromResult((byte[])null);
                }
                _logger.Debug($"S7 DBRead succeeded: read {buffer.Length} bytes");
                return Task.FromResult(buffer);
            }
            catch (FormatException ex)
            {
                _logger.Error($"Format exception in S7 DBRead for DB{dbNumber} at offset {byteOffset}: {ex.Message}. Stack trace: {ex.StackTrace}");
                return Task.FromResult((byte[])null);
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception in S7 DBRead for DB{dbNumber} at offset {byteOffset}: {ex.Message}. Stack trace: {ex.StackTrace}", ex);
                return Task.FromResult((byte[])null);
            }
        }

        private Task Write(int dbNumber, int byteOffset, byte[] values)
        {
            // Sharp7 DBWrite returns an error code (0 = success)
            int result = _S7.DBWrite(dbNumber, byteOffset, values.Length, values);
            if (result != 0)
            {
                _logger.Error($"S7 DBWrite failed for DB{dbNumber} at offset {byteOffset}: error code {result}");
                throw new Exception($"S7 DBWrite failed with error code {result}");
            }
            return Task.CompletedTask;
        }

        private int GetDataTypeByteSize(string dataType)
        {
            return dataType switch
            {
                "Boolean" => 1,
                "Integer" => 2,  // Default to Int16 (2 bytes), can be Int32 (4 bytes) if needed
                "Float" => 4,
                "String" => 256,  // Default string length, should be configurable
                _ => 4  // Default to 4 bytes
            };
        }

        public string ExecuteAction(MethodState method, IList<object> inputArgs, ref IList<object> outputArgs)
        {
            throw new NotImplementedException();
        }
    }
}


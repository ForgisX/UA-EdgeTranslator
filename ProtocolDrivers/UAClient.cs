namespace Opc.Ua.Edge.Translator.ProtocolDrivers
{
    using Opc.Ua;
    using Opc.Ua.Client;
    using Opc.Ua.Client.ComplexTypes;
    using Opc.Ua.Edge.Translator.Interfaces;
    using Opc.Ua.Edge.Translator.Models;
    using Opc.Ua.Edge.Translator.Logging;
    using Serilog;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Text;
    using System.Threading.Tasks;

    public class UAClient : IAsset
    {
        private readonly ILogger _logger = ClientLogger.ForClient("OPCUA");
        private ISession _session = null;
        private string _endpoint = string.Empty;

        private List<SessionReconnectHandler> _reconnectHandlers = new List<SessionReconnectHandler>();
        private object _reconnectHandlersLock = new object();

        private Dictionary<string, uint> _missedKeepAlives = new Dictionary<string, uint>();
        private object _missedKeepAlivesLock = new object();

        private readonly Dictionary<ISession, ComplexTypeSystem> _complexTypeList = new Dictionary<ISession, ComplexTypeSystem>();

        public List<string> Discover()
        {
            List<string> discoveredServers = new();

            // connect to an OPC UA Global Discovery Server
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPC_UA_GDS_ENDPOINT_URL")))
            {
                var client = DiscoveryClient.Create(new Uri(Environment.GetEnvironmentVariable("OPC_UA_GDS_ENDPOINT_URL")));

                var servers = client.FindServers(null);
                foreach (var server in servers)
                {
                    _logger.Information($"Server: {server.ApplicationName}");
                    foreach (var endpoint in server.DiscoveryUrls)
                    {
                        discoveredServers.Add(endpoint);
                    }
                }
            }

            return discoveredServers;
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

                // TODO: Add support for browsing OPC UA nodes and generating properties/actions
            };

            return td;
        }

        public void Connect(string ipAddress, int port)
        {
            var url = "opc.tcp://" + ipAddress + ":" + port;
            _logger.Information($"Connecting to OPC UA server at {url}");
            var username = Environment.GetEnvironmentVariable("OPCUA_CLIENT_USERNAME");
            var password = Environment.GetEnvironmentVariable("OPCUA_CLIENT_PASSWORD");
            if (string.IsNullOrEmpty(username))
            {
                _logger.Debug("Using anonymous authentication for OPC UA connection");
            }
            else
            {
                _logger.Debug($"Using username/password authentication for OPC UA connection");
            }
            ConnectSessionAsync(url, username, password).GetAwaiter().GetResult();
        }

        public void Disconnect()
        {
            if (_session != null)
            {
                _session.Close();
                _session = null;
            }
        }

        public string GetRemoteEndpoint()
        {
            return _endpoint;
        }

        public object Read(AssetTag tag)
        {
            _logger.Debug($"Reading OPC UA tag: {tag.Name} from address: {tag.Address}");
            object rawValue = Read(tag.Address).GetAwaiter().GetResult();

            if (rawValue == null)
            {
                _logger.Warning($"OPC UA read returned null for tag {tag.Name} at address {tag.Address}");
                return null;
            }
            
            _logger.Debug($"OPC UA read successful: tag={tag.Name}, value={rawValue}");

            try
            {
                return tag.Type switch
                {
                    "Float" => Convert.ToSingle(rawValue),
                    "Boolean" => Convert.ToBoolean(rawValue),
                    "Integer" => Convert.ToInt32(rawValue),
                    "String" => Convert.ToString(rawValue),
                    _ => rawValue
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to convert OPC UA value for tag {tag.Name}: {ex.Message}", ex);
                return null;
            }
        }

        public void Write(AssetTag tag, string value)
        {
            object typedValue;
            if (tag.Type == "Float")
            {
                typedValue = float.Parse(value);
            }
            else if (tag.Type == "Boolean")
            {
                typedValue = bool.Parse(value);
            }
            else if (tag.Type == "Integer")
            {
                typedValue = int.Parse(value);
            }
            else if (tag.Type == "String")
            {
                typedValue = value;
            }
            else
            {
                throw new ArgumentException("Type not supported by OPC UA.");
            }

            Write(tag.Address, typedValue).GetAwaiter().GetResult();
        }

        private Task<object> Read(string addressWithinAsset)
        {
            if (_session != null)
            {
                try
                {
                    var nodeId = ExpandedNodeId.ToNodeId(new ExpandedNodeId(addressWithinAsset), _session.NamespaceUris);
                    _logger.Debug($"Reading OPC UA node: {nodeId} (from address: {addressWithinAsset})");
                    var value = _session.ReadValue(nodeId);
                    
                    if (value.StatusCode != StatusCodes.Good)
                    {
                        _logger.Warning($"OPC UA read returned bad status code: {value.StatusCode} for node {nodeId}");
                    }

                    return Task.FromResult(value.Value);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception reading OPC UA node {addressWithinAsset}: {ex.Message}", ex);
                    return Task.FromResult<object>(null);
                }
            }
            else
            {
                _logger.Error("OPC UA session is null, cannot read node");
                throw new Exception("OPC UA session is null, cannot read node");
            }
        }

        private Task Write(string addressWithinAsset, object value)
        {
            WriteValue nodeToWrite = new()
            {
                NodeId = new NodeId(addressWithinAsset),
                Value = new DataValue(new Variant(value))
            };

            WriteValueCollection nodesToWrite = new(){ nodeToWrite };

            RequestHeader requestHeader = new()
            {
                ReturnDiagnostics = (uint)DiagnosticsMasks.All
            };

            StatusCodeCollection results = null;
            DiagnosticInfoCollection diagnosticInfos = null;

            var responseHeader = _session.Write(
                requestHeader,
                nodesToWrite,
                out results,
                out diagnosticInfos);

            ClientBase.ValidateResponse(results, nodesToWrite);
            ClientBase.ValidateDiagnosticInfos(diagnosticInfos, nodesToWrite);

            if (StatusCode.IsBad(results[0]))
            {
                throw ServiceResultException.Create(results[0], 0, diagnosticInfos, responseHeader.StringTable);
            }

            return Task.CompletedTask;
        }

        private async Task ConnectSessionAsync(string endpointUrl, string username, string password)
        {
            _endpoint = endpointUrl;

            // check if the required session is already available
            if (_session != null && _session.Endpoint.EndpointUrl == endpointUrl)
            {
                return;
            }

            var selectedEndpoint = CoreClientUtils.SelectEndpoint(Program.App.ApplicationConfiguration, endpointUrl, true);
            var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(Program.App.ApplicationConfiguration));

            var timeout = (uint)Program.App.ApplicationConfiguration.ClientConfiguration.DefaultSessionTimeout;

            UserIdentity userIdentity = null;
            if (username == null)
            {
                userIdentity = new UserIdentity(new AnonymousIdentityToken());
            }
            else
            {
                userIdentity = new UserIdentity(username, password);
            }

            try
            {
                _session = await Session.Create(
                    Program.App.ApplicationConfiguration,
                    configuredEndpoint,
                    true,
                    false,
                    Program.App.ApplicationConfiguration.ApplicationName,
                    timeout,
                    userIdentity,
                    null
                ).ConfigureAwait(false);
                _logger.Information($"Successfully connected to OPC UA server at {endpointUrl}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to connect to OPC UA server at {endpointUrl}: {ex.Message}", ex);
                throw;
            }

            // enable diagnostics
            _session.ReturnDiagnostics = DiagnosticsMasks.All;

            // register keep alive callback
            _session.KeepAlive += KeepAliveHandler;

            // enable subscriptions transfer
            _session.DeleteSubscriptionsOnClose = false;
            _session.TransferSubscriptionsOnReconnect = true;


            // load complex type system
            try
            {
                if (!_complexTypeList.ContainsKey(_session))
                {
                    _complexTypeList.Add(_session, new ComplexTypeSystem(_session));
                }

                await _complexTypeList[_session].Load().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
            }
        }

        private void KeepAliveHandler(ISession session, KeepAliveEventArgs eventArgs)
        {
            if (eventArgs != null && session != null && session.ConfiguredEndpoint != null)
            {
                try
                {
                    var endpoint = session.ConfiguredEndpoint.EndpointUrl.AbsoluteUri;

                    lock (_missedKeepAlivesLock)
                    {
                        if (!ServiceResult.IsGood(eventArgs.Status))
                        {
                            if (session.Connected)
                            {
                                // add a new entry, if required
                                if (!_missedKeepAlives.ContainsKey(endpoint))
                                {
                                    _missedKeepAlives.Add(endpoint, 0);
                                }

                                _missedKeepAlives[endpoint]++;
                            }

                            // start reconnect if there are 3 missed keep alives
                            if (_missedKeepAlives[endpoint] >= 3)
                            {
                                // check if a reconnection is already in progress
                                var reconnectInProgress = false;
                                lock (_reconnectHandlersLock)
                                {
                                    foreach (var handler in _reconnectHandlers)
                                    {
                                        if (ReferenceEquals(handler.Session, session))
                                        {
                                            reconnectInProgress = true;
                                            break;
                                        }
                                    }
                                }

                                if (!reconnectInProgress)
                                {
                                    var reconnectHandler = new SessionReconnectHandler();
                                    lock (_reconnectHandlersLock)
                                    {
                                        _reconnectHandlers.Add(reconnectHandler);
                                    }
                                    reconnectHandler.BeginReconnect(session, 10000, ReconnectCompleteHandler);
                                }
                            }
                        }
                        else
                        {
                            if (_missedKeepAlives.ContainsKey(endpoint) && _missedKeepAlives[endpoint] != 0)
                            {
                                // Reset missed keep alive count
                                _missedKeepAlives[endpoint] = 0;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.Message, ex);
                }
            }
        }

        private void ReconnectCompleteHandler(object sender, EventArgs e)
        {
            // find our reconnect handler
            SessionReconnectHandler reconnectHandler = null;
            lock (_reconnectHandlersLock)
            {
                foreach (var handler in _reconnectHandlers)
                {
                    if (ReferenceEquals(sender, handler))
                    {
                        reconnectHandler = handler;
                        break;
                    }
                }
            }

            // ignore callbacks from discarded objects
            if (reconnectHandler == null || reconnectHandler.Session == null)
            {
                return;
            }

            // update the session
            _session = reconnectHandler.Session;


            lock (_reconnectHandlersLock)
            {
                _reconnectHandlers.Remove(reconnectHandler);
            }
            reconnectHandler.Dispose();
        }

        public string ExecuteAction(MethodState method, IList<object> inputArgs, ref IList<object> outputArgs)
        {
            CallMethodRequestCollection requests = new CallMethodRequestCollection
            {
                new CallMethodRequest
                {
                    ObjectId = new NodeId(method.Parent.NodeId),
                    MethodId = method.NodeId
                }
            };

            if (inputArgs != null)
            {
                requests[0].InputArguments = new VariantCollection();

                foreach (var arg in inputArgs)
                {
                    requests[0].InputArguments.Add(new Variant(arg));
                }
            }

            CallMethodResultCollection results;
            DiagnosticInfoCollection diagnosticInfos;

            ResponseHeader responseHeader = _session.Call(
                null,
                requests,
                out results,
                out diagnosticInfos);

            ClientBase.ValidateResponse(results, requests);
            ClientBase.ValidateDiagnosticInfos(diagnosticInfos, requests);

            StatusCode status = new StatusCode(0);
            if ((results != null) && (results.Count > 0))
            {
                status = results[0].StatusCode;

                if (StatusCode.IsBad(results[0].StatusCode) && (responseHeader.StringTable != null) && (responseHeader.StringTable.Count > 0))
                {
                    return responseHeader.StringTable[0];
                }

                if ((results[0].OutputArguments != null) && (results[0].OutputArguments.Count > 0))
                {
                    outputArgs = new List<object>(results[0].OutputArguments.Count);

                    for (int i = 0; i < results[0].OutputArguments.Count; i++)
                    {
                        outputArgs.Add(results[0].OutputArguments[i].Value);
                    }
                }
            }

            return "Action executed successfully.";
        }
    }
}

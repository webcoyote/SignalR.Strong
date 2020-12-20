﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace SignalR.Strong
{
    public class StrongClient
    {
        private IServiceProvider _provider;
        private ProxyGenerator _proxyGenerator = new ProxyGenerator();
        private Dictionary<System.Type, HubConnection> _hubConnections = new Dictionary<Type, HubConnection>();
        private Dictionary<System.Type, object> _hubs = new Dictionary<Type, object>();
        private Dictionary<System.Type, System.Type> _spokeToHubMapping = new Dictionary<Type, Type>();
        private Dictionary<System.Type, System.Type> _spokeToImplMapping = new Dictionary<Type, Type>();
        private Dictionary<System.Type, List<IDisposable>> _spokeToHandlerRegistrationsMapping = new Dictionary<Type, List<IDisposable>>();
        private Dictionary<System.Type, object> _spokes = new Dictionary<Type, object>();

        public bool IsBuilt { get; private set; }
        
        public bool IsConnected { get; private set; }

        public StrongClient()
        {

        }

        public StrongClient(IServiceProvider provider)
        {
            _provider = provider;
        }

        public StrongClient RegisterSpoke<TSpoke, THub>()
        {
            return this.RegisterSpoke<TSpoke, TSpoke, THub>();
        }
        
        public StrongClient RegisterSpoke<TSpokeIntf, TSpokeImpl, THub>()
            where TSpokeImpl : TSpokeIntf
        {
            if(IsBuilt) throw new AccessViolationException("Can not map spokes after the client has been built");
            _spokeToHubMapping.Add(typeof(TSpokeIntf), typeof(THub));
            _spokeToImplMapping.Add(typeof(TSpokeIntf), typeof(TSpokeImpl));
            _spokeToHandlerRegistrationsMapping.Add(typeof(TSpokeIntf), new List<IDisposable>());
            return this;
        }

        public StrongClient RegisterSpoke<TSpokeIntf, TSpokeImpl, THub>(TSpokeImpl spoke)
            where TSpokeImpl : TSpokeIntf
        {
            if(IsBuilt) throw new AccessViolationException("Can not map spokes after the client has been built");
            _spokeToHubMapping.Add(typeof(TSpokeIntf), typeof(THub));
            _spokeToImplMapping.Add(typeof(TSpokeIntf), typeof(TSpokeImpl));
            _spokeToHandlerRegistrationsMapping.Add(typeof(TSpokeIntf), new List<IDisposable>());
            _spokes.Add(typeof(TSpokeIntf), spoke);
            return this;
        }

        public StrongClient RegisterHub<THub>(HubConnection hubConnection) where THub : class
        {
            if (IsBuilt) throw new AccessViolationException("Can not map spokes after the client has been built");
            var hubInterceptor = new HubInterceptor(hubConnection);
            var hubProxy = _proxyGenerator.CreateInterfaceProxyWithoutTarget<THub>(hubInterceptor.ToInterceptor()); 
            _hubConnections[typeof(THub)] = hubConnection;
            _hubs[typeof(THub)] = hubProxy;
            return this;
        }

        public THub GetHub<THub>()
        {
            return (THub)this.GetHub(typeof(THub));
        }
        
        public object GetHub(Type hubType)
        {
            return _hubs[hubType];
        }

        public HubConnection GetConnection<THub>()
        {
            return this.GetConnection(typeof(THub));
        }
        
        public HubConnection GetConnection(Type hubType)
        {
            return _hubConnections[hubType];
        }

        public TSpoke GetSpoke<TSpoke>()
        {
            if(!IsBuilt) throw new AccessViolationException("Client must first be built!");
            return (TSpoke)_spokes[typeof(TSpoke)];
        }

        public async Task<StrongClient> ConnectToHubsAsync()
        {
            foreach (var connection in _hubConnections.Values)
            {
                await connection.StartAsync();
            }

            IsConnected = true;
            return this;
        }

        public StrongClient BuildSpokes()
        {
            IsBuilt = true;
            foreach (var types in _spokeToHubMapping)
            {
                var intfType = types.Key;
                var implType = this._spokeToImplMapping[intfType];
                var hubType = types.Value;
                buildSpoke(intfType, implType, hubType);
            }
            return this;
        }

        private void buildSpoke(Type intfType, Type implType, Type hubType)
        {
            if (!_spokes.TryGetValue(intfType, out object spoke))
            {
                // If spoke doesn't exist, create it
                if (_provider is null)
                {
                    spoke = Activator.CreateInstance(implType);
                }
                else
                {
                    // Perform dependency injection if a service provider is given
                    spoke = ActivatorUtilities.CreateInstance(_provider, implType);
                }
            }
            
            var connection = _hubConnections[hubType];
            
            if (typeof(ISpoke).IsAssignableFrom(implType))
            {
                // If spoke implements the spoke interface, then set its properties
                var ispoke = (ISpoke) spoke;
                ispoke.Connection = connection;
                ispoke.Client = this;
                ispoke.WeakHub = this.GetHub(hubType);
            }

            var methods = intfType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            foreach (var method in methods)
            {
                var reg = connection.On(method.Name, method.GetParameters().Select(a => a.ParameterType).ToArray(),
                    objects =>
                    {
                        method.Invoke(spoke, objects);
                        return Task.CompletedTask;
                    });
                _spokeToHandlerRegistrationsMapping[intfType].Add(reg);
            }

            _spokes[intfType] = spoke;
        }
    }
}

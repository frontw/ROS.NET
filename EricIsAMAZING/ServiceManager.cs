﻿#region Using

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Messages;
using XmlRpc_Wrapper;
using m = Messages.std_msgs;
using gm = Messages.geometry_msgs;
using nm = Messages.nav_msgs;

#endregion

namespace Ros_CSharp
{
    public class ServiceManager
    {
        private static ServiceManager _instance;
        private static object g_service_manager_mutex = new object();
        private bool shutting_down;
        private List<IServicePublication> service_publications = new List<IServicePublication>();
        private List<IServiceServerLink> service_server_links = new List<IServiceServerLink>();
        private PollManager poll_manager;
        private ConnectionManager connection_manager;
        private XmlRpcManager xmlrpc_manager;
        private object service_publications_mutex = new object(), service_server_links_mutex = new object(), shutting_down_mutex = new object();

        ~ServiceManager()
        {
            shutdown();
        }

        public static ServiceManager Instance
        {
            get
            {
                if (_instance == null) _instance = new ServiceManager();
                return _instance;
            }
        }

        internal IServicePublication lookupServicePublication(string name)
        {
            lock (service_publications_mutex)
            {
                foreach (IServicePublication sp in service_publications)
                {
                    if (sp.name == name)
                        return sp;
                }
            }
            return null;
        }

        internal ServiceServerLink<M, T> createServiceServerLink<M, T>(string service, bool persistent, string request_md5sum, string response_md5sum, IDictionary header_values)
            where M : IRosMessage, new()
            where T : IRosMessage, new()
        {
            lock (shutting_down_mutex)
            {
                if (shutting_down)
                    return null;
            }

            int serv_port = -1;
            string serv_host = "";
            if (!lookupService(service, ref serv_host, ref serv_port))
                return null;
            TcpTransport transport = new TcpTransport(poll_manager.poll_set);
            if (transport.connect(serv_host, serv_port))
            {
                Connection connection = new Connection();
                connection_manager.addConnection(connection);
                ServiceServerLink<M,T> client = new ServiceServerLink<M,T>(service, persistent, request_md5sum, response_md5sum, header_values);
                lock (service_server_links_mutex)
                    service_server_links.Add(client);
                connection.initialize(transport, false, null);
                client.initialize(connection);
                return client;
            }
            return null;
        }

        internal void removeServiceServerLink<M, T>(ServiceServerLink<M, T> issl)
            where M : IRosMessage, new()
            where T : IRosMessage, new() { removeServiceServerLink((IServiceServerLink)issl); } 
        internal void removeServiceServerLink(IServiceServerLink issl)
        {
            if (shutting_down) return;
            lock (service_server_links_mutex)
            {
                if (service_server_links.Contains(issl))
                    service_server_links.Remove(issl);
            }
        }

        internal bool advertiseService<MReq, MRes>(AdvertiseServiceOptions<MReq, MRes> ops) where MReq : IRosMessage, new() where MRes : IRosMessage, new()
        {
            lock (shutting_down_mutex)
            {
                if (shutting_down)
                    return false;
            }
            lock (service_publications_mutex)
            {
                if (isServiceAdvertised(ops.service))
                {
                    EDB.WriteLine("Tried to advertise  a service that is already advertised in this node [{0}]", ops.service);
                    return false;
                }
                if (ops.helper == null)
                    ops.helper = new ServiceCallbackHelper<MReq, MRes>(ops.srv_func);
                ServicePublication<MReq, MRes> pub = new ServicePublication<MReq, MRes>(ops.service, ops.md5sum, ops.datatype, ops.req_datatype, ops.res_datatype, ops.helper, ops.callback_queue, ops.tracked_object);
                service_publications.Add((IServicePublication)pub);
            }

            XmlRpcValue args = new XmlRpcValue(), result = new XmlRpcValue(), payload = new XmlRpcValue();
            args.Set(0, this_node.Name);
            args.Set(1, ops.service);
            args.Set(2, string.Format("rosrpc://{0}:{1}", network.host, connection_manager.TCPPort));
            args.Set(3, xmlrpc_manager.uri);
            master.execute("registerService", args, ref result, ref payload, true);
            return true;
        }

        internal bool unadvertiseService(string service)
        {
            lock (shutting_down_mutex)
            {
                if (shutting_down)
                    return false;
            }
            IServicePublication pub = null;
            lock (service_publications_mutex)
            {
                foreach (IServicePublication sp in service_publications)
                {
                    if (sp.name == service && !sp.isDropped)
                    {
                        pub = sp;
                        service_publications.Remove(sp);
                        break;
                    }
                }
            }
            if (pub != null)
            {
                unregisterService(pub.name);
                pub.drop();
                return true;
            }
            return false;
        }

      /*  internal IServiceServerLink createServiceServerLink(string name, bool persistent, string md5sum, string md5sum_2,
                                                            IDictionary header_values)
        {
            lock (shutting_down_mutex)
            {
                if (shutting_down)
                    return null;
            }

            int serv_port=-1;
            string serv_host="";
            if (!lookupService(name, ref serv_host, ref serv_port))
                return null;
            TcpTransport transport = new TcpTransport(poll_manager.poll_set);
            if (transport.connect(serv_host, serv_port))
            {
                Connection connection = new Connection();
                connection_manager.addConnection(connection);
                IServiceServerLink client = new IServiceServerLink(name, persistent, md5sum, md5sum_2, header_values);
                lock (service_server_links_mutex)
                    service_server_links.Add(client);
                connection.initialize(transport, false, null);
                client.initialize(connection);
                return client;
            }
            return null;
        }*/

        internal void shutdown()
        {
            lock (shutting_down_mutex)
            {
                if (shutting_down)
                    return;
            }
            shutting_down = true;
            lock (service_publications_mutex)
            {
                foreach (IServicePublication sp in service_publications)
                {
                    unregisterService(sp.name);
                    sp.drop();
                }
                service_publications.Clear();
            }
            List<IServiceServerLink> local_service_clients;
            lock (service_server_links)
            {
                local_service_clients = new List<IServiceServerLink>(service_server_links);
                service_server_links.Clear();
            }
            foreach (IServiceServerLink issl in local_service_clients)
            {
                issl.connection.drop(Connection.DropReason.Destructing);
            }
            local_service_clients.Clear();
        }

        public void Start()
        {
            shutting_down = false;
            poll_manager = PollManager.Instance;
            connection_manager = ConnectionManager.Instance;
            xmlrpc_manager = XmlRpcManager.Instance;
        }

        private bool isServiceAdvertised(string serv_name)
        {
            List<IServicePublication> sp = new List<IServicePublication>(service_publications);
            return sp.Any(s => s.name == serv_name && !s.isDropped);
        }

        private bool unregisterService(string service)
        {
            XmlRpcValue args = new XmlRpcValue(), result = new XmlRpcValue(), payload = new XmlRpcValue();
            args.Set(0, this_node.Name);
            args.Set(1, service);
            args.Set(2, string.Format("rosrpc://{0}:{1}", network.host, connection_manager.TCPPort));
            return master.execute("unregisterService", args, ref result, ref payload, false);
        }

        internal bool lookupService(string name, ref string serv_host, ref int serv_port)
        {
            XmlRpcValue args = new XmlRpcValue(), result = new XmlRpcValue(), payload = new XmlRpcValue();
            args.Set(0, this_node.Name);
            args.Set(1, name);
            if (!master.execute("lookupService", args, ref result, ref payload, false))
                return false;
            string serv_uri = payload.GetString();
            if (serv_uri.Length == 0)
            {
                EDB.WriteLine("lookupService: Empty server URI returned from master");
                return false;
            }
            if (!network.splitURI(serv_uri, ref serv_host, ref serv_port))
            {
                EDB.WriteLine("lookupService: Bad service uri [{0}]", serv_uri);
                return false;
            }
            return true;
        }

        internal bool lookUpService(string mapped_name, string host, int port)
        {
            throw new NotImplementedException();
        }

        internal bool lookUpService(string mapped_name, ref string host, ref int port)
        {
            throw new NotImplementedException();
        }
    }
}
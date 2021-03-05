﻿using Microsoft.FlightSimulator.SimConnect;
using SimConnectHelper.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace SimConnectHelper
{
    /// <summary>
    /// Static class to connect/disconnect to MSFS 2020, via SimConnect
    /// </summary>
    public static class SimConnectHandler
    {
        private static MessageHandler handler = null;
        private static CancellationTokenSource source = null;
        private static CancellationToken token = CancellationToken.None;
        private static Task messagePump;
        private static EndPoint endPoint;
        private static AutoResetEvent messagePumpRunning = new AutoResetEvent(false);
        private static SimConnect simConnect = null;
        private const int WM_USER_SIMCONNECT = 0x0402;
        private static int RequestID = 0;
        public static Dictionary<int, SimConnectVariable> Requests { get; private set; } = new Dictionary<int, SimConnectVariable>();
        public static bool UseFSXcompatibleConnection { get; set; } = false;
        public static bool FSConnected { get; private set; } = false;
        public static SimConnectConfig Connection { get; private set; }
        /// <summary>
        /// Called whenever SimConnect connects or disconnects with MSFS 2020
        /// </summary>
        public static EventHandler<bool> SimConnected;
        /// <summary>
        /// Called whenever SimConnect receives an error from MSFS 2020
        /// </summary>
        public static EventHandler<IOException> SimError;
        /// <summary>
        /// Called whenever MSFS 2020 transmits requested data about an object (e.g. SimVar result)
        /// </summary>
        public static EventHandler<SimConnectVariableValue> SimData;
        /// <summary>
        /// How often should SimConnect update the values for requested SimVars
        /// </summary>
        public static SimConnectUpdateFrequency DefaultUpdateFrequency { get; set; } = SimConnectUpdateFrequency.SIM_Frame;
        /// <summary>
        /// Full path and filename to use for saving a Config file
        /// </summary>
        public static string ConfigFilePath { get; set; } = Path.Combine(Environment.CurrentDirectory, "SimConnect.cfg");

        /// <summary>
        /// Attempts to connect to a local instance of MSFS 2020, using each XML-defined connection, until one connects
        /// </summary>
        public static void Connect()
        {
            foreach(var config in GetLocalFSConnections())
            {
                Connect(config);
                var timeout = DateTime.Now.AddSeconds(1);
                while (!FSConnected && DateTime.Now < timeout)
                    Thread.Sleep(50);
                if (FSConnected)
                    break;
            }
        }

        /// <summary>
        /// Creates a SimConnect.cfg file and attempts to connect SimConnect to MSFS 2020.
        /// Only creates a SimConnect.cfg if Server IP or Port are different from last connection
        /// </summary>
        /// <param name="ep">MSFS 2020 SimConnect Server IP & Port, NULL forces the re-use of a previously saved Config</param>
        public static void Connect(EndPoint ep)
        {
            if (ep != null)
                endPoint = ep;
            Connect((SimConnectConfig)null);
        }

        public static void Connect(SimConnectConfig config)
        {
            if (source != null)
                Disconnect();
            Connection = config;
            CreateConfigFile(config);
            source = new CancellationTokenSource();
            token = source.Token;
            token.ThrowIfCancellationRequested();
            messagePump = new Task(RunMessagePump, token);
            messagePump.Start();
            messagePumpRunning = new AutoResetEvent(false);
            messagePumpRunning.WaitOne();
        }

        /// <summary>
        /// Will stop the Message Handler from running, thereby ignoring all events from SimConnect
        /// </summary>
        public static void Disconnect()
        {
            StopMessagePump();
            // Raise event to notify client we've disconnected
            SimConnect_OnRecvQuit(simConnect, null);
            simConnect?.Dispose(); // May have already been disposed or not even been created, e.g. Disconnect called before Connect
            simConnect = null;
        }

        private static void StopMessagePump()
        {
            if (source != null && token.CanBeCanceled)
            {
                source.Cancel();
            }
            if (messagePump != null)
            {
                handler.Stop();
                handler = null;

                messagePumpRunning.Close();
                messagePumpRunning.Dispose();
            }
            messagePump = null;
        }

        /// <summary>
        /// Will attempt to delete an existing SimConnect.cfg file, if not in use
        /// </summary>
        /// 
        private static bool DeleteConfigFile()
        {
            var filePath = GetConfigFilePath();// Path.Combine(Environment.CurrentDirectory, "SimConnect.cfg");
            if (File.Exists(filePath))
                try
                {
                    File.Delete(filePath);
                }
                catch { return false; }
            return true;
        }

        private static void CreateConfigFile(SimConnectConfig config = null)
        {
            if (config == null)
                config = new SimConnectConfig
                {
                    Descr = "Dynamic Config",
                    Address = ((IPEndPoint)endPoint).Address.ToString(),
                    Port = ((IPEndPoint)endPoint).Port.ToString()
                };
            var filePath = GetConfigFilePath();
            File.WriteAllText(filePath, config.ConfigFileText);
        }

        /// <summary>
        /// If not already connected, will attempt to connect ONCE to MSFS2020
        /// </summary>
        private static void RunMessagePump()
        {
            // Create control to handle windows messages
            if (!FSConnected)
            {
                handler = new MessageHandler();
                handler.CreateHandle();
                ConnectFS(handler);
            }
            messagePumpRunning.Set();
            Application.Run();
        }

        /// <summary>
        /// Create an instance of SimConnect, if successful, attaches all event handlers
        /// </summary>
        /// <param name="messageHandler">Windows Message Handler</param>
        private static void ConnectFS(MessageHandler messageHandler)
        {
            // SimConnect must be linked in the same thread as the Application.Run()
            try
            {
                simConnect = new SimConnect("RemoteClient", messageHandler.Handle, WM_USER_SIMCONNECT, null, UseFSXcompatibleConnection ? (uint)1 : 0);

                messageHandler.MessageReceived += MessageReceived;

                /// Listen for Connect
                simConnect.OnRecvOpen += new SimConnect.RecvOpenEventHandler(SimConnect_OnRecvOpen);

                /// Listen for Disconnect
                simConnect.OnRecvQuit += new SimConnect.RecvQuitEventHandler(SimConnect_OnRecvQuit);

                /// Listen for Exceptions
                simConnect.OnRecvException += new SimConnect.RecvExceptionEventHandler(SimConnect_OnRecvException);

                /// Listen for SimVar Data
                simConnect.OnRecvSimobjectDataBytype += new SimConnect.RecvSimobjectDataBytypeEventHandler(SimConnect_OnRecvSimobjectDataBytype);

                /// Listen for SimVar Data
                simConnect.OnRecvSimobjectData += new SimConnect.RecvSimobjectDataEventHandler(SimConnect_OnRecvSimobjectData);
            }
            catch { } // Is MSFS is not running, a COM Exception is raised. We ignore it!
        }

        private static void SimConnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (simConnect != null) {
                var newData = new SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE
                {
                    dwData = data.dwData,
                    dwDefineCount = data.dwDefineCount,
                    dwDefineID = data.dwDefineID,
                    dwentrynumber = data.dwentrynumber,
                    dwFlags = data.dwFlags,
                    dwID = data.dwID,
                    dwObjectID = data.dwObjectID,
                    dwoutof = data.dwoutof,
                    dwRequestID = data.dwRequestID,
                    dwSize = data.dwSize,
                    dwVersion = data.dwVersion
                };
                SimConnect_OnRecvSimobjectDataBytype(sender, newData);
            }
        }

        /// <summary>
        /// When SimConnect sends an updated object, the data is captured here
        /// </summary>
        /// <param name="sender">SimConnect</param>
        /// <param name="data">Object Data</param>
        private static void SimConnect_OnRecvSimobjectDataBytype(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            if (SimData != null)
                try
                {
                    var simVarVal = new SimConnectVariableValue
                    {
                        Request = Requests[(int)data.dwRequestID],
                        Value = data?.dwData
                    };
                    SimData.DynamicInvoke(simConnect, simVarVal);
                }
                catch// (Exception ex)
                {

                }
        }

        /// <summary>
        /// When SimConnect encounters an error, it is captured here.
        /// </summary>
        /// <param name="sender">SimConnect</param>
        /// <param name="data">Error details</param>
        private static void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            if (SimError != null)
                try
                {
                    var ex = new IOException("SimConnect returned an Error, details in Data", null);
                    ex.Source = "SimConnect";
                    ex.Data.Add("data", data);
                    SimError.DynamicInvoke(simConnect, ex);
                }
                catch { }
        }

        /// <summary>
        /// When SimConnect successfully connects to MSFS 2020, this event is triggered
        /// </summary>
        /// <param name="sender">SimConnect</param>
        /// <param name="data">Connection info</param>
        private static void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            FSConnected = true;
            if (SimConnected != null)
                try
                {
                    SimConnected.DynamicInvoke(simConnect, true);
                }
                catch { }
        }

        /// <summary>
        /// When SimConnect loses connection to MSFS 2020, this event is triggered.
        /// </summary>
        /// <param name="sender">SimConnect</param>
        /// <param name="data">connection data</param>
        private static void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            FSConnected = false;
            if (SimConnected != null)
                try
                {
                    SimConnected.DynamicInvoke(simConnect, false);
                }
                catch { }
        }

        /// <summary>
        /// Request a SimVariable from SimConnect, optionally start capturing values
        /// </summary>
        /// <param name="request">SimVar to fetch from SimConnect</param>
        /// <param name="frequency">How frequently should SimConnect provide an updated value?</param>
        /// <returns>A unique ID for the submitted request. Use this to request the next value via FetchValueUpdate</returns>
        public static int GetSimVar(SimConnectVariable request, SimConnectUpdateFrequency frequency = SimConnectUpdateFrequency.Never)
        {
            if (FSConnected)
            {
                var unit = request.Unit;
                if (unit?.IndexOf("string") > -1)
                {
                    unit = null;
                }
                SimVarRequest simReq;
                lock (Requests)
                {
                    if (Requests.Any(x => x.Value.Name.Equals(request.Name, StringComparison.InvariantCultureIgnoreCase)
                        && x.Value.Unit.Equals(request.Unit, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        // Re-use a previously requested variable for retransmission to SimConnect
                        var reqId = GetRequestId(request);
                        simReq = new SimVarRequest
                        {
                            ID = reqId,
                            Request = request
                        };
                    }
                    else
                    {
                        // Fetch the values suitable for transmission to SimConnect
                        simReq = new SimVarRequest
                        {
                            ID = RequestID++,
                            Request = request
                        };
                        // New SimVar requested - add it to our list
                        Requests.Add((int)simReq.ReqID, simReq.Request);
                    }
                }
                // Submit the SimVar request to SimConnect
                simConnect.AddToDataDefinition(simReq.DefID, request.Name, unit, simReq.SimType, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                // Tell SimConnect what type of value we are expecting to be returned
                switch (simReq.Type?.FullName)
                {
                    case "System.Double":
                        simConnect.RegisterDataDefineStruct<double>(simReq.DefID);
                        break;
                    case "System.UInt16":
                    case "System.UInt32":
                    case "System.UInt64":
                        simConnect.RegisterDataDefineStruct<uint>(simReq.DefID);
                        break;
                    case "System.Int16":
                    case "System.Int32":
                        simConnect.RegisterDataDefineStruct<int>(simReq.DefID);
                        break;
                    case "System.Boolean":
                        simConnect.RegisterDataDefineStruct<bool>(simReq.DefID);
                        break;
                    case "System.Byte":
                        simConnect.RegisterDataDefineStruct<byte>(simReq.DefID);
                        break;
                    case "System.String":
                        simConnect.RegisterDataDefineStruct<SimVarString>(simReq.DefID);
                        break;
                    default:
                        simConnect.RegisterDataDefineStruct<object>(simReq.DefID); // This will likely fail as variants don't transform well
                        break;
                }
                if (frequency != SimConnectUpdateFrequency.Never)
                    GetSimVar(simReq.ID, DefaultUpdateFrequency); // Request value to be sent back immediately, will auto-update using pre-defined frequency
                return simReq.ID;
            }
            return -1;
        }

        /// <summary>
        /// Allows cancelling of a previously requested variable, if it is no-longer needed
        /// </summary>
        /// <param name="request">SimVar Request to cancel</param>
        public static bool CancelRequest(SimConnectVariable request)
        {
            var result = false;
            if (simConnect != null && FSConnected && Requests.Any(x => x.Value.Name == request.Name && x.Value.Unit == request.Unit))
            {
                lock (Requests)
                {
                    try
                    {
                        var submittedRequest = Requests.First(x => x.Value.Name == request.Name && x.Value.Unit == request.Unit);
                        var requestId = submittedRequest.Key;
                        simConnect.ClearDataDefinition((SIMVARDEFINITION)requestId);
                        simConnect.ClearClientDataDefinition((SIMVARDEFINITION)requestId);
                        Requests.Remove(requestId);
                        result = true;
                    }
                    catch { }
                }
            }
            return result;
        }

        /// <summary>
        /// Request an update for a specific SimVar request
        /// </summary>
        /// <param name="requestID">ID returned by SendRequest</param>
        /// <param name="frequency">SimVar can be requested manually (SimConnectUpdateFrequency.Never) or auto-sent at a pre-defined frequency</param>
        public static void GetSimVar(int requestID, SimConnectUpdateFrequency frequency = SimConnectUpdateFrequency.Never)
        {
            try
            {
                if (FSConnected)
                    if (frequency == SimConnectUpdateFrequency.Never)
                        simConnect?.RequestDataOnSimObjectType((SIMVARREQUEST)requestID, (SIMVARDEFINITION)requestID, 0, SIMCONNECT_SIMOBJECT_TYPE.USER);
                    else
                    {
                        SIMCONNECT_PERIOD period = Enum.Parse<SIMCONNECT_PERIOD>(frequency.ToString().ToUpper());
                        simConnect?.RequestDataOnSimObject((SIMVARREQUEST)requestID, (SIMVARDEFINITION)requestID, 0, period, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    }
            }
            catch// (Exception ex)
            {
                // Likely cause, no request for this variable has previously been submitted
            }
        }

        /// <summary>
        /// Request an update for a specific SimVar request (used for GetSimVar(frequency = SIMCONNECT_PERIOD.NEVER))
        /// </summary>
        /// <param name="requestID">Variable definition requested via GetSimVar</param>
        public static void GetSimVar(SimConnectVariable request)
        {
            var reqId = GetRequestId(request);
            if (reqId > -1)
            {
                GetSimVar(reqId);
            }
            else
            {
                GetSimVar(request, SimConnectUpdateFrequency.Never);
            }
        }

        /// <summary>
        /// Request an update for a specific SimVar request ID returned by GetSimVar(frequency = SIMCONNECT_PERIOD.NEVER)
        /// </summary>
        /// <param name="requestId">ID returned when submitting the original SimVar request</param>
        public static void GetSimVar(int requestId)
        {
            if(requestId > -1)
                GetSimVar(requestId, SimConnectUpdateFrequency.Never);
        }

        /// <summary>
        /// Set the value associated with a SimVar
        /// </summary>
        /// <param name="simVarValue">SimVar and associated value</param>
        public static void SetSimVar(SimConnectVariableValue simVarValue)
        {
            // As for requests, setting values is a 2-step process, reserve the data area,then modify the data it holds
            GetSimVar(simVarValue.Request);
            var reqId = GetRequestId(simVarValue.Request);
            if (reqId > -1)
            {
                // Data area reserved, now set the value
                simConnect.SetDataOnSimObject((SIMVARDEFINITION)reqId, (uint)reqId, (SIMCONNECT_DATA_SET_FLAG)SimConnect.SIMCONNECT_OBJECT_ID_USER, simVarValue.Value);
            }
        }

        private static int GetRequestId(SimConnectVariable request)
        {
            return Requests.Any(x =>
                x.Value.Name.Equals(request.Name, StringComparison.InvariantCultureIgnoreCase)
                && x.Value.Unit.Equals(request.Unit, StringComparison.InvariantCultureIgnoreCase)) ?
                    Requests.FirstOrDefault(x =>
                        x.Value.Name.Equals(request.Name, StringComparison.InvariantCultureIgnoreCase)
                        && x.Value.Unit.Equals(request.Unit, StringComparison.InvariantCultureIgnoreCase)).Key
                : -1;
        }

        /// <summary>
        /// Every Windowws Message is captured here, we check for SimConnect messages and process them, else we ignore it
        /// </summary>
        /// <param name="sender">Windows Object generating the message</param>
        /// <param name="msg">Message from sender</param>
        private static void MessageReceived(object sender, Message msg)
        {
            if (msg.Msg == WM_USER_SIMCONNECT && simConnect != null)
                try
                {
                    // SimConnect has something to tell us - ask it to raise the relevant event
                    if (simConnect != null)
                        simConnect.ReceiveMessage();
                }
                catch// (Exception ex)
                {
                    // Seems to happen if FS is shutting down or when we disconnect
                }
        }

        /// <summary>
        /// Full path & filename to the SimConnect.cfg file
        /// </summary>
        /// <returns>SimConnect.cfg FilePath</returns>
        private static string GetConfigFilePath()
        {
            // Need to confirm the correct locaion for SimConnct.cfg.
            // Some documentation states it is in the AppData folder, others within the current folder, others still state the My Documents folder
            //var filePath = Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Microsoft Flight Simulator", "SimConnect.cfg");
            return ConfigFilePath;
        }

        private static List<SimConnectConfig> GetLocalFSConnections()
        {
            List<SimConnectConfig> configs = new List<SimConnectConfig>();
            try
            {
                var filePath = Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "Microsoft Flight Simulator", "SimConnect.xml");
                var fileContent = File.ReadAllText(filePath); // Load the file content instead of loading as XML - overcomes limitation with encoding
                XmlDocument xml = new XmlDocument();
                xml.LoadXml(fileContent);
                XmlNodeList xmlNodeList = xml.SelectNodes("/SimBase.Document/SimConnect.Comm");
                foreach (XmlNode xmlNode in xmlNodeList)
                {
                    try
                    {
                        SimConnectConfig config = GetConfigFromXml(xmlNode);
                        configs.Add(config);
                    }
                    catch { }
                }
            }
            catch
            {
            }
            return configs;
        }

        private static SimConnectConfig GetConfigFromXml(XmlNode xmlNode)
        {
            XmlSerializer serial = new XmlSerializer(typeof(SimConnectConfig));
            using (XmlNodeReader reader = new XmlNodeReader(xmlNode))
            {
                return (SimConnectConfig)serial.Deserialize(reader);
            }
        }
    }
}

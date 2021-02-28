using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimConnectHelper;
using SimConnectHelper.Common;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SimConnectHandler_Tests
{
    /// <summary>
    /// Tests will only succeed if MSFS 2020 is currently running
    /// </summary>
    [TestClass]
    public class SimConnectHandler_Tests
    {
        private const string MSFSServerName = "localhost";
        private const int MSFSServerPort = 500;

        private SimConnectVariableValue result = null;

        [TestMethod]
        public void ConnectUseLocalServerConfig_Test()
        {
            SimConnectHandler.Connect(); // Find/Try all defined server connection configurations
            Assert.IsTrue(SimConnectHandler.FSConnected);
        }

        [TestMethod]
        public void ConnectConfiguration_Test()
        {
            SimConnectHandler.Connect(); // Find/Try all defined server connection configurations
            Assert.IsNotNull(SimConnectHandler.Connection);
        }

        [TestMethod]
        public void ConnectViaIP_Test()
        {
            SimConnectHandler.Connect(GetEndPoint());
            Thread.Sleep(1000);
            Assert.IsTrue(SimConnectHandler.FSConnected);
        }

        [TestMethod]
        public void RequestSimVar_Test()
        {
            result = null;
            SimConnectHandler.SimError += SimConnect_Error;
            SimConnectHandler.SimConnected += SimConnect_Connection;
            SimConnectHandler.SimData += SimConnect_DataReceived;
            SimConnectHandler.Connect();
            var variable = new SimConnectVariable
            {
                Name = "AMBIENT WIND VELOCITY",
                Unit = "knots"
            };
            var requestID = SimConnectHandler.SendRequest(variable, true);

            // Wait up to 5 seconds for MSFS to return the requested value
            DateTime endTime = DateTime.Now.AddSeconds(5);
            while (result == null && DateTime.Now < endTime)
            {
                Thread.Sleep(100);
            }
            Assert.IsNotNull(result);
        }

        private void SimConnect_DataReceived(object sender, SimConnectVariableValue e)
        {
            result = e;
        }

        private void SimConnect_Connection(object sender, bool e)
        {
            Assert.IsTrue(e);
        }

        private void SimConnect_Error(object sender, IOException e)
        {
            throw new NotImplementedException();
        }

        private EndPoint GetEndPoint()
        {
            var ipAddress = Dns.GetHostAddresses(MSFSServerName).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            return new IPEndPoint(ipAddress, MSFSServerPort);
        }
    }
}

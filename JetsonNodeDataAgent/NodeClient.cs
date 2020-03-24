using System;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
using RestSharp;
using System.Collections.Generic;

namespace JetsonNodeDataAgent
{
    /// <summary>
    /// <see cref="NodeClient"/> represents a single node in the cluster and obtain and sends
    /// utilization statistics to the master node.
    /// </summary>
    class NodeClient
    {
        private String host_name;
        private int num_cores;
        private float[] cpu_usage;      //%
        private uint used_mem;          //MB
        private uint total_mem;         //MB
        private int frequency;          //Hz
        private String JetsonServiceIP; // Standard IPv4 address
        private uint NodeID, ClusterID;
        private RestClient ServiceClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="NodeClient"/> class.
        /// </summary>
        /// <remarks>
        /// Data is obtained from NodeClientConfig.txt or automatically.
        /// </remarks>
        public NodeClient()
        {
            string ConfigFile = System.IO.File.ReadAllText(@"NodeClientConfig.txt");
            string[] SplitConfigFile = ConfigFile.Split(new Char[] { '\n' });

            JetsonServiceIP = SplitConfigFile[0].Replace("JetsonServiceIP=", "");
            NodeID = UInt32.Parse(SplitConfigFile[1].Replace("NodeID=", ""));
            ClusterID = UInt32.Parse(SplitConfigFile[2].Replace("ClusterID=", ""));
            frequency = Int32.Parse(SplitConfigFile[3].Replace("Frequency=", ""));
            host_name = Dns.GetHostName();
            num_cores = DetermineNumCores();
            total_mem = DetermineMemTotal();
            cpu_usage = new float[num_cores];
            ServiceClient = new RestClient("https://" + JetsonServiceIP);
        }

        public static string GetLocalIPAddress() // source: https://stackoverflow.com/questions/6803073/get-local-ip-address
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }

        /// <summary>
        /// SendData currently prints out node values for debugging purposes, but in its final
        /// form, it will transmit JSON files containing the data to the master node running
        /// JetsonService.
        /// </summary>
        private void SendData(Object stateInfo)
        {
            var node = new Node()
            {
                Id = NodeID,
                IPAddress = GetLocalIPAddress()
            };

            var cores = new List<CpuCore>();
            for (int i = 0; i < cpu_usage.Length; i++)
            {
                cores.Add(new CpuCore() { CoreNumber = i, UtilizationPercentage = cpu_usage[i] });
            }

            var nodeUtilization = new NodeUtilization()
            {
                GlobalNodeId = ClusterID * 100 + NodeID,
                MemoryAvailable = total_mem - used_mem,
                MemoryUsed = used_mem,
                TimeStamp = DateTime.Now,
                Cores = cores,
            };

            var request = new RestRequest();

            request.Method = Method.POST;
            request.AddHeader("Accept", "application/json");
            request.Parameters.Clear();
            request.AddJsonBody(node);

            ServiceClient.Execute(request);

            request = new RestRequest();

            request.Method = Method.POST;
            request.AddHeader("Accept", "application/json");
            request.Parameters.Clear();
            request.AddJsonBody(nodeUtilization);

            ServiceClient.Execute(request);
        }

        /// <summary>
        /// UpdateMemory updates the value of the used memory.
        /// </summary>
        private void UpdateMemory()
        {
            //string cat_proc_meminfo_output = Bash("cat /proc/meminfo");
            string cat_proc_meminfo_output = System.IO.File.ReadAllText(@"/proc/meminfo");
            cat_proc_meminfo_output = cat_proc_meminfo_output.Replace(" ", "");     //remove spaces
            cat_proc_meminfo_output = cat_proc_meminfo_output.Replace("kB", "");    //remove kB

            string[] entries = cat_proc_meminfo_output.Split(new Char[] { '\n' });

            foreach (string s in entries)
            {
                if (s.Contains("MemFree"))
                {
                    string ss = s.Replace("MemFree:", "");
                    used_mem = total_mem - UInt32.Parse(ss);
                    break;
                }
            }
        }

        /// <summary>
        /// UpdateCPUUsage updates the value of each core's CPU usage.
        /// </summary>
        private void UpdateCPUUsage()
        {
            // Find phase 1 then phase 2 in order to find change.

            //string cat_proc_stat_output_phase1 = Bash("cat /proc/stat");
            string cat_proc_stat_output_phase1 = System.IO.File.ReadAllText(@"/proc/stat");
            uint[] active_cpu_phase1 = new uint[num_cores];
            uint[] total_cpu_phase1 = new uint[num_cores];
            
            for (int i = 0; i < num_cores; i++)
            {
                active_cpu_phase1[i] = 0;
                total_cpu_phase1[i] = 0;
            }

            string[] entries1 = cat_proc_stat_output_phase1.Split(new Char[] { '\n' });

            for (int i = 1; i <= num_cores; i++)
            {
                string current_core_entry = entries1[i];
                string[] each_value = current_core_entry.Split(new Char[] { ' ' });
                for (int j = 1; j <= each_value.Length - 1; j++)
                {
                    total_cpu_phase1[i - 1] += UInt32.Parse(each_value[j]);
                    if (j != 4 && j != 5)   // not idle or iowait
                        active_cpu_phase1[i - 1] += UInt32.Parse(each_value[j]);
                }
            }

            Thread.Sleep(500 / frequency);  //wait half period

            // Find phase 2.

            //string cat_proc_stat_output_phase2 = Bash("cat /proc/stat");
            string cat_proc_stat_output_phase2 = System.IO.File.ReadAllText(@"/proc/stat");
            uint[] active_cpu_phase2 = new uint[num_cores];
            uint[] total_cpu_phase2 = new uint[num_cores];

            for (int i = 0; i < num_cores; i++)
            {
                active_cpu_phase2[i] = 0;
                total_cpu_phase2[i] = 0;
            }

            string[] entries2 = cat_proc_stat_output_phase2.Split(new Char[] { '\n' });

            for (int i = 1; i <= num_cores; i++)
            {
                string current_core_entry = entries2[i];
                string[] each_value = current_core_entry.Split(new Char[] { ' ' });
                for (int j = 1; j <= each_value.Length - 1; j++)
                {
                    total_cpu_phase2[i - 1] += UInt32.Parse(each_value[j]);
                    if (j != 4 && j != 5)   // not idle or iowait
                        active_cpu_phase2[i - 1] += UInt32.Parse(each_value[j]);
                }
            }

            // Calculate each core's usage

            for (int i = 0; i < num_cores; i++)
            {
                cpu_usage[i] = (float)(active_cpu_phase2[i] - active_cpu_phase1[i]) / (total_cpu_phase2[i] - total_cpu_phase1[i]);
            }
        }

        private void Update(Object stateInfo)
        {
            UpdateMemory();
            UpdateCPUUsage();
        }

        /// <summary>
        /// DetermineNumCores finds the number of cores on the system.
        /// </summary>
        private int DetermineNumCores()
        {
            return Int32.Parse(Bash("grep ^proc /proc/cpuinfo | wc -l"));
        }

        /// <summary>
        /// DetermineMemTotal finds the total amount of memory of the system.
        /// </summary>
        private uint DetermineMemTotal()
        {
            //string cat_proc_meminfo_output = Bash("cat /proc/meminfo");
            string cat_proc_meminfo_output = System.IO.File.ReadAllText(@"/proc/meminfo");
            cat_proc_meminfo_output = cat_proc_meminfo_output.Replace(" ", "");     //remove spaces
            cat_proc_meminfo_output = cat_proc_meminfo_output.Replace("kB", "");    //remove kB

            string[] entries = cat_proc_meminfo_output.Split(new Char[] { '\n' });

            foreach (string s in entries)
            {
                if (s.Contains("MemTotal"))
                {
                    string ss = s.Replace("MemTotal:", "");
                    return UInt32.Parse(ss);
                }
            }

            return 0;
        }

        /// <summary>
        /// Bash is a helper function which executes a bash command and returns the output
        /// as a string.
        /// </summary>
        /// /// <param name="cmd"></param>
        public static string Bash(string cmd)      // Adapted from https://loune.net/2017/06/running-shell-bash-commands-in-net-core/
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return result;
        }

        /// <summary>
        /// Main will update the data and transmit the data to JetsonService in an
        /// infinite loop.
        /// </summary>
        static void Main(string[] args)
        {
            NodeClient myProgram = new NodeClient();

            var autoEventUpdate = new AutoResetEvent(false);
            var autoEventSend = new AutoResetEvent(false);

            var UpdateTimer = new Timer(myProgram.Update, autoEventUpdate, 0, 1000 / myProgram.frequency);
            var SendTimer = new Timer(myProgram.SendData, autoEventUpdate, 1000 / myProgram.frequency, 1000 / myProgram.frequency);

            autoEventUpdate.WaitOne();  // neither AutoResetEvent objects will ever be set, so the threads will run indefinitely.
            autoEventSend.WaitOne();
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading;
using Nancy;
using Nancy.Hosting.Self;
using Nancy.Diagnostics;
using Newtonsoft.Json;
using Nancy.Testing;
using RestSharp;

public class UpdateMessage
{
    public uint CID { get; set; }   // Cluster ID
    public uint NID { get; set; }   // Node ID
    public uint freemem { get; set; }   // MB
    public uint usedmem { get; set; }   // MB
    public String NIP { get; set; }  // IPv4 address
    public float[] cpuutil { get; set; }    // %
    public String OS { get; set; }   // name of operating system
    public TimeSpan utime { get; set; } // uptime of the node
    public int frequency { get; set; }   //Hz
}

namespace JetsonNodeDataAgent
{
    /// <summary>
    /// <see cref="NodeClient"/> represents a single node in the cluster and obtain and sends
    /// utilization statistics to the master node.
    /// </summary>
    static class NodeClient
    {
        public static String host_name;
        public static int num_cores;
        public static float[] cpu_usage;
        public static uint used_mem;
        public static uint total_mem;
        public static int frequency;
        public static String JetsonServiceIP;
        public static uint NodeID;
        public static uint ClusterID;
        public static String OperatingSystem;
        public static TimeSpan UpTime;
        public static UpdateMessage currentMessage;

        /// <summary>
        /// Initializes a new instance of the <see cref="NodeClient"/> class.
        /// </summary>
        /// <remarks>
        /// Data is obtained from NodeClientConfig.txt or automatically.
        /// </remarks>
        public static void Init()
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
            OperatingSystem = Environment.OSVersion.VersionString.ToString();
            currentMessage = new UpdateMessage();
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
        /// SendData transmits JSON files containing the data to the master node running
        /// JetsonService.
        /// </summary>
        public static void SendData(Object stateInfo)
        {
            currentMessage.CID = ClusterID;
            currentMessage.NID = NodeID;
            currentMessage.freemem = total_mem - used_mem;
            currentMessage.usedmem = used_mem;
            currentMessage.NIP = GetLocalIPAddress();
            currentMessage.cpuutil = cpu_usage;
            currentMessage.OS = OperatingSystem;
            currentMessage.utime = UpTime;
            currentMessage.frequency = frequency;

            var client = new RestClient("http://" + JetsonServiceIP);
            var request = new RestRequest("/nodeupdate", Method.PUT);
            request.AddJsonBody(currentMessage);
            client.Execute(request);
        }

        /// <summary>
        /// UpdateMemory updates the value of the used memory.
        /// </summary>
        private static void UpdateMemory()
        {
            string proc_meminfo_output = System.IO.File.ReadAllText(@"/proc/meminfo");
            //string proc_meminfo_output = System.IO.File.ReadAllText(@"fakeprocmeminfo.txt");
            proc_meminfo_output = proc_meminfo_output.Replace(" ", "");     //remove spaces
            proc_meminfo_output = proc_meminfo_output.Replace("kB", "");    //remove kB

            string[] entries = proc_meminfo_output.Split(new Char[] { '\n' });

            foreach (string s in entries)
            {
                if (s.Contains("MemFree"))
                {
                    string ss = s.Replace("MemFree:", "");
                    used_mem = (uint)(total_mem - (float.Parse(ss) / 1024f));   // convert to MB then subtract from total_mem
                    break;
                }
            }
        }

        /// <summary>
        /// UpdateCPUUsage updates the value of each core's CPU usage.
        /// </summary>
        private static void UpdateCPUUsage()
        {
            // Find phase 1 then phase 2 in order to find change.

            string proc_stat_output_phase1 = System.IO.File.ReadAllText(@"/proc/stat");
            //string proc_stat_output_phase1 = System.IO.File.ReadAllText(@"fakeprocstat.txt");
            uint[] active_cpu_phase1 = new uint[num_cores];
            uint[] total_cpu_phase1 = new uint[num_cores];
            
            for (int i = 0; i < num_cores; i++)
            {
                active_cpu_phase1[i] = 0;
                total_cpu_phase1[i] = 0;
            }

            string[] entries1 = proc_stat_output_phase1.Split(new Char[] { '\n' });

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

            string proc_stat_output_phase2 = System.IO.File.ReadAllText(@"/proc/stat");
            //string proc_stat_output_phase2 = System.IO.File.ReadAllText(@"fakeprocstat.txt");
            uint[] active_cpu_phase2 = new uint[num_cores];
            uint[] total_cpu_phase2 = new uint[num_cores];

            for (int i = 0; i < num_cores; i++)
            {
                active_cpu_phase2[i] = 0;
                total_cpu_phase2[i] = 0;
            }

            string[] entries2 = proc_stat_output_phase2.Split(new Char[] { '\n' });

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
                cpu_usage[i] = 100f * (float)(active_cpu_phase2[i] - active_cpu_phase1[i]) / (total_cpu_phase2[i] - total_cpu_phase1[i]);
            }
        }

        /// <summary>
        /// UpdateUpTime updates the node's uptime.
        /// </summary>
        private static void UpdateUpTime()
        {
            string proc_uptime_output = System.IO.File.ReadAllText(@"/proc/uptime");
            //string proc_uptime_output = "350735.47 234388.90";
            string[] entries = proc_uptime_output.Split(new Char[] { ' ' });
            UpTime = TimeSpan.FromSeconds(Double.Parse(entries[0]));
        }

        public static void Update(Object stateInfo)
        {
            UpdateMemory();
            UpdateCPUUsage();
            UpdateUpTime();
        }

        /// <summary>
        /// DetermineNumCores finds the number of cores on the system.
        /// </summary>
        private static int DetermineNumCores()
        {
            return Int32.Parse(Bash("grep ^proc /proc/cpuinfo | wc -l"));
            //return 2;
        }

        /// <summary>
        /// DetermineMemTotal finds the total amount of memory of the system.
        /// </summary>
        private static uint DetermineMemTotal()
        {
            string proc_meminfo_output = System.IO.File.ReadAllText(@"/proc/meminfo");
            //string proc_meminfo_output = System.IO.File.ReadAllText(@"fakeprocmeminfo.txt");
            proc_meminfo_output = proc_meminfo_output.Replace(" ", "");     //remove spaces
            proc_meminfo_output = proc_meminfo_output.Replace("kB", "");    //remove kB

            string[] entries = proc_meminfo_output.Split(new Char[] { '\n' });

            foreach (string s in entries)
            {
                if (s.Contains("MemTotal"))
                {
                    string ss = s.Replace("MemTotal:", "");
                    //return UInt32.Parse(ss) / 1024; // convert to MB
                    return (uint)(float.Parse(ss) / 1024f);
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
    }

    public class Program
    {
        /// <summary>
        /// Main will update the data and transmit the data to JetsonService in an
        /// infinite loop.
        /// </summary>
        static void Main(string[] args)
        {
            NodeClient.Init();

            var autoEventUpdate = new AutoResetEvent(false);
            var autoEventSend = new AutoResetEvent(false);

            var UpdateTimer = new Timer(NodeClient.Update, autoEventUpdate, 0, 1000 / NodeClient.frequency);
            var SendTimer = new Timer(NodeClient.SendData, autoEventUpdate, 1000 / NodeClient.frequency, 1000 / NodeClient.frequency);

            autoEventUpdate.WaitOne();  // neither AutoResetEvent objects will ever be set, so the threads will run indefinitely.
            autoEventSend.WaitOne();
        }
    }
}

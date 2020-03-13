﻿using System;
using System.Net;
using System.Diagnostics;
using System.Threading;

namespace JetsonNodeDataAgent
{
    class Program
    {
        private String host_name;
        private int num_cores;
        private float[] cpu_usage;      //%
        private uint used_mem;          //MB
        private uint total_mem;         //MB
        private int frequency;          //Hz

        public Program()
        {
            frequency = 1;                  //1 Hz is default frequency
            host_name = Dns.GetHostName();
            num_cores = DetermineNumCores();
            total_mem = DetermineMemTotal();
            cpu_usage = new float[num_cores];
        }

        public void ChangeFrequency(int freq) => frequency = freq;  //replace eventually with thread that listens for JSON from master. Also make threadsafe for that.

        private void SendData()
        {
            //replace eventually with generating JSON with data and sending it via HTTPS to master node
            Console.WriteLine("Host name = " + host_name);
            Console.WriteLine("Number of cores = " + num_cores.ToString());
            Console.WriteLine("Total memory = " + total_mem.ToString());
            Console.WriteLine("Used memory = " + used_mem.ToString());
            for (int i = 0; i < num_cores; i++)
            {
                Console.WriteLine("Core " + i.ToString() + " Usage = " + cpu_usage[i].ToString() + "%.");
            }
        }

        private void UpdateMemory()
        {
            string cat_proc_meminfo_output = Bash("cat /proc/meminfo");
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

        private void UpdateCPUUsage()
        {
            // Find phase 1 then phase 2 in order to find change.

            string cat_proc_stat_output_phase1 = Bash("cat /proc/stat");
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

            string cat_proc_stat_output_phase2 = Bash("cat /proc/stat");
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

        private int DetermineNumCores()
        {
            return Int32.Parse(Bash("grep ^proc /proc/cpuinfo | wc -l"));
        }

        private uint DetermineMemTotal()
        {
            string cat_proc_meminfo_output = Bash("cat /proc/meminfo");
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

        static void Main(string[] args)
        {
            Program myProgram = new Program();
            while(true)
            {
                Thread.Sleep(500 / myProgram.frequency);    // Wait half period. Other half in CPU usage function
                myProgram.UpdateMemory();
                myProgram.SendData();
            }
        }
    }
}

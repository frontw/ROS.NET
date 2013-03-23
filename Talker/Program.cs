﻿#region Imports

using System;
using System.IO;
using System.Threading;
using Messages;
using Messages.custom_msgs;
using Ros_CSharp;
using XmlRpc_Wrapper;
using Int32 = Messages.std_msgs.Int32;
using String = Messages.std_msgs.String;
using m = Messages.std_msgs;
using gm = Messages.geometry_msgs;
using nm = Messages.nav_msgs;
using sm = Messages.sensor_msgs;
using System.Text;

#endregion

namespace videoView
{
    public class Program
    {
        private static void Main(string[] args)
        {
            ROS.ROS_MASTER_URI = "http://10.0.2.88:11311";  
            ROS.ROS_HOSTNAME = "10.0.2.47";
            ROS.Init(args, "Talker");
            NodeHandle node = new NodeHandle();
            Publisher<m.String> Talker = node.advertise<m.String>("/Chatter", 1);
            int count = 0;
            while (true)
            {
                String pow = new String("Blah blah blah "+(count++));
                
                Talker.publish(pow);
                ROS.spinOnce(node);
                Thread.Sleep(100);
            }
        }
    }
}
﻿#region Imports

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Diagnostics;
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
using Messages.arm_status_msgs;
using Messages.rock_publisher;
using System.IO;

// for threading
using System.Windows.Threading;

// for controller; don't forget to include Microsoft.Xna.Framework in References
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

// for timer
using System.Timers;

#endregion

namespace WpfApplication1
{
    public partial class MainWindow : Window
    {

        private DetectionHelper[] detectors;

        //checks is rocks restored
        bool rocks_restored = false;

        // controller
        GamePadState currentState;

        // nodes
        NodeHandle nh;

        // timer near end, timer ended
        bool end, near;

        // initialize timer values; 1 full hour
        int hours = 1, minutes = 0, seconds = 0;

        // initialize rock count values to 0
        int red = 0, orange = 0, yellow = 0, green = 0, blue = 0, purple = 0;
        
        // ring buffer for rocks, main camera index, sub camera index
        int rockRing = 0, incrementValue;

        Publisher<m.Byte> multiplexPub;
        Publisher<gm.Twist> velPub;

        TabItem[] mainCameras, subCameras;
        ROS_ImageWPF.CompressedImageControl[] mainImages;
        ROS_ImageWPF.SlaveImage[] subImages;

        DispatcherTimer controllerUpdater;

        private const int back_cam = 1;
        private const int front_cam = 0;
        //Stopwatch
        Stopwatch sw = new Stopwatch();

        private bool _adr;

        private void adr(bool status)
        {
            if (_adr != status)
            {
                _adr = status;
                mainImages[back_cam].Transform(status ? -1 : 1, 1);
                subImages[back_cam].Transform(status ? -1 : 1, 1);
                subImages[front_cam].Transform(status ? 1 : -1, 1);
            }
        }
        
        // initialize stuff for MainWindow
        public MainWindow()
        {
            InitializeComponent();
        }


        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            controllerUpdater = new DispatcherTimer() { Interval = new TimeSpan(0, 0, 0, 0, 100) };
            controllerUpdater.Tick += Link;
            controllerUpdater.Start();

            _trans.Label = "Linear Max Vel";
            _trans.Max = 2.0;
            _trans.Min = 0.0;
            _trans.Value = 0.5;

            _rot.Label = "Angular Max Vel";
            _rot.Max = 3.14;
            _rot.Min = 0.0;
            _rot.Value = 0.5;

            new Thread(() =>
            {
                // ROS stuff
                ROS.ROS_MASTER_URI = "http://10.0.3.88:11311";
                ROS.Init(new string[0], "The_UI_" + System.Environment.MachineName.Replace("-", "__"));
                nh = new NodeHandle();
                Dispatcher.Invoke(new Action(() =>
                {
                    armGauge.startListening(nh);
                    battvolt.startListening(nh);
                    EStop.startListening(nh);
                }));

                // instantiating some global helpers
                detectors = new DetectionHelper[4];
                for (int i = 0; i < 4; ++i)
                {
                    detectors[i] = new DetectionHelper(nh, i, this);
                }

                velPub = nh.advertise<gm.Twist>("/cmd_vel", 1);
                multiplexPub = nh.advertise<m.Byte>("/cam_select", 1);

                new Thread(() =>
                {
                    while (!ROS.shutting_down)
                    {
                        ROS.spinOnce(ROS.GlobalNodeHandle);
                        Thread.Sleep(10);
                    }
                }).Start();

                Dispatcher.Invoke(new Action(() =>
                {
                    mainCameras = new TabItem[] { MainCamera1, MainCamera2, MainCamera3, MainCamera4 };
                    subCameras = new TabItem[] { SubCamera1, SubCamera2, SubCamera3, SubCamera4 };
                    mainImages = new ROS_ImageWPF.CompressedImageControl[mainCameras.Length];
                    subImages = new ROS_ImageWPF.SlaveImage[subCameras.Length];
                    for (int i = 0; i < mainCameras.Length; i++)
                    {
                        mainImages[i] = (mainCameras[i].Content as ROS_ImageWPF.CompressedImageControl);
                        subImages[i] = (subCameras[i].Content as ROS_ImageWPF.SlaveImage);
                        mainImages[i].AddSlave(subImages[i]);
                    }
                    subCameras[1].Focus();
                    adr(false);
                }));

                // drawing boxes, i hope this is the right place to do that
                int x = 0, y = 0;
                while (ROS.ok)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        mainImages[0].DrawABox(new System.Windows.Point((x++) % (int)Math.Round(mainImages[0].ActualWidth), (y++) % (int)Math.Round(mainImages[0].ActualHeight)), 10, 10, mainImages[0].ActualWidth, mainImages[0].ActualHeight);
                    }));
                    Thread.Sleep(100);
                }
            }).Start();
        }

        // close ros when application closes
        protected override void OnClosed(EventArgs e)
        {
            ROS.shutdown();
            base.OnClosed(e);
        }

        // timer delegate
        public void Timer(object dontcare, EventArgs alsodontcare)
        {
            // display timer
            TimerTextBlock.Text = "Elapsed: " + hours.ToString("D2") + ':' + minutes.ToString("D2") + ':' + seconds.ToString("D2");

            // change timer textblock to yellow when near = true;
            if (near == true)
                TimerTextBlock.Foreground = Brushes.Yellow;

            // change timer textblock to red when end = true
            if (end == true)
            {
                TimerTextBlock.Foreground = Brushes.Red;

                // display end of time in timer textblock
                TimerStatusTextBlock.Text = "END OF TIME";
                // display this in timer status textblock when end is true
                TimerTextBlock.Text = "Press Right Stick to restart";
                TimerStatusTextBlock.Foreground = Brushes.Red;
            }
        }

        // controller link dispatcher
        public void Link(object sender, EventArgs dontcare)
        {
            // get state of player one
            currentState = GamePad.GetState(PlayerIndex.One);

            // if controller is connected...
            if (currentState.IsConnected)
            {
                // ...say its connected in the textbloxk, color it green cuz its good to go
                LinkTextBlock.Text = "Controller: Connected";
                LinkTextBlock.Foreground = Brushes.Green;

                foreach (Buttons b in Enum.GetValues(typeof(Buttons)))
                {
                    Button(b);
                }
                double y = currentState.ThumbSticks.Left.Y;
                double x = currentState.ThumbSticks.Left.X;
                if (_adr)
                {
                    x *= -1;
                    y *= -1;
                }
                gm.Twist vel = new gm.Twist { linear = new gm.Vector3 { x = y * _trans.Value }, angular = new gm.Vector3 { z = x * _rot.Value } };
                if(velPub != null)
                    velPub.publish(vel);
            }
            // unless if controller is not connected...
            else if (!currentState.IsConnected)
            {
                // ...have program complain controller is disconnected
                LinkTextBlock.Text = "Controller: Disconnected";
                LinkTextBlock.Foreground = Brushes.Red;
            }
        }

        private byte maincameramask, secondcameramask;

        // when MainCameraControl tabs are selected
        private void MainCameraTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            maincameramask = (byte)Math.Round(Math.Pow(2.0, MainCameraTabControl.SelectedIndex));

            //enter ADR?
            if (MainCameraTabControl.SelectedIndex == back_cam)
            {
                SubCameraTabControl.SelectedIndex = front_cam;
                adr(true);
            }
            else
                adr(false);

            JimCarry();
        }

        // When SubCameraTabControl tabs are selected
        private void SubCameraTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            secondcameramask = (byte)Math.Round(Math.Pow(2.0, SubCameraTabControl.SelectedIndex));
            JimCarry();
        }


        //What an odd name to give a function...
        private void JimCarry()
        {
            if (multiplexPub == null) return;
            m.Byte msg = new m.Byte { data = (byte)(maincameramask | secondcameramask) };
            Console.WriteLine("SENDING CAM SELECT: " + msg.data);
            multiplexPub.publish(msg);
        }

        // stuff that happens when to move arond the ring buffer
        void ringSwitch()
        {
            switch (rockRing)
            {
                // bold text block for red rocks and normalizes neighboring text blocks
                case 0:
                    PurpleTextBlock.FontWeight = FontWeights.Normal;
                    RedTextBlock.FontWeight = FontWeights.UltraBold;
                    OrangeTextBlock.FontWeight = FontWeights.Normal;

                    PurpleRock.Stroke = Brushes.Black;
                    RedRock.Stroke = Brushes.White;
                    OrangeRock.Stroke = Brushes.Black;

                    PurpleRock.StrokeThickness = 1;
                    RedRock.StrokeThickness = 4;
                    OrangeRock.StrokeThickness = 1;
                    return;
                // bold text block for orange rocks and normalizes neighboring text blocks
                case 1:
                    RedTextBlock.FontWeight = FontWeights.Normal;
                    OrangeTextBlock.FontWeight = FontWeights.UltraBold;
                    YellowTextBlock.FontWeight = FontWeights.Normal;

                    RedRock.Stroke = Brushes.Black;
                    OrangeRock.Stroke = Brushes.White;
                    YellowRock.Stroke = Brushes.Black;

                    RedRock.StrokeThickness = 1;
                    OrangeRock.StrokeThickness = 4;
                    YellowRock.StrokeThickness = 1;
                    return;
                // bold text block for yellow rocks and normalizes neighboring text blocks
                case 2:
                    OrangeTextBlock.FontWeight = FontWeights.Normal;
                    YellowTextBlock.FontWeight = FontWeights.UltraBold;
                    GreenTextBlock.FontWeight = FontWeights.Normal;

                    OrangeRock.Stroke = Brushes.Black;
                    YellowRock.Stroke = Brushes.White;
                    GreenRock.Stroke = Brushes.Black;

                    OrangeRock.StrokeThickness = 1;
                    YellowRock.StrokeThickness = 4;
                    GreenRock.StrokeThickness = 1;
                    return;
                // bold text block for green rocks and normalizes neighboring text blocks
                case 3:
                    YellowTextBlock.FontWeight = FontWeights.Normal;
                    GreenTextBlock.FontWeight = FontWeights.UltraBold;
                    BlueTextBlock.FontWeight = FontWeights.Normal;

                    YellowRock.Stroke = Brushes.Black;
                    GreenRock.Stroke = Brushes.White;
                    BlueRock.Stroke = Brushes.Black;

                    YellowRock.StrokeThickness = 1;
                    GreenRock.StrokeThickness = 4;
                    BlueRock.StrokeThickness = 1;
                    return;
                // bold text block for blue rocks and normalizes neighboring text blocks
                case 4:
                    GreenTextBlock.FontWeight = FontWeights.Normal;
                    BlueTextBlock.FontWeight = FontWeights.UltraBold;
                    PurpleTextBlock.FontWeight = FontWeights.Normal;

                    GreenRock.Stroke = Brushes.Black;
                    BlueRock.Stroke = Brushes.White;
                    PurpleRock.Stroke = Brushes.Black;

                    GreenRock.StrokeThickness = 1;
                    BlueRock.StrokeThickness = 4;
                    PurpleRock.StrokeThickness = 1;
                    return;
                // bold text block for purple rocks and normalizes neighboring text blocks
                case 5:
                    BlueTextBlock.FontWeight = FontWeights.Normal;
                    PurpleTextBlock.FontWeight = FontWeights.UltraBold;
                    RedTextBlock.FontWeight = FontWeights.Normal;

                    BlueRock.Stroke = Brushes.Black;
                    PurpleRock.Stroke = Brushes.White;
                    RedRock.Stroke = Brushes.Black;

                    BlueRock.StrokeThickness = 1;
                    PurpleRock.StrokeThickness = 4;
                    RedRock.StrokeThickness = 1;
                    return;
            }
        }

        // the function that changes rock count
        void rockIncrement()
        {
            string[] rocks;
            if (rocks_restored == false) { rocks = new string[] { "0", "0,", "0", "0", "0", "0" }; rock_file_writer(rocks); rocks_restored = true; Rock_Restorer.Visibility = Visibility.Hidden; }
            switch (rockRing)
            {
                // change red count and display it
                case 0:
                    red = red + incrementValue;
                    if (red < 0)
                        red = 0;
                    RedCount.Text = red.ToString();
                    RedCountShadow.Text = red.ToString();
                    rocks = rock_file_reader();
                    rocks[0] = red.ToString();
                    rock_file_writer(rocks);
                    return;
                // change red count and display it
                case 1:
                    orange = orange + incrementValue;
                    if (orange < 0)
                        orange = 0;
                    OrangeCount.Text = orange.ToString();
                    OrangeCountShadow.Text = orange.ToString();
                    rocks = rock_file_reader();
                    rocks[1] = orange.ToString();
                    rock_file_writer(rocks);
                    return;
                // change red count and display it
                case 2:
                    yellow = yellow + incrementValue;
                    if (yellow < 0)
                        yellow = 0;
                    YellowCount.Text = yellow.ToString();
                    YellowCountShadow.Text = yellow.ToString();
                    rocks = rock_file_reader();
                    rocks[2] = yellow.ToString();
                    rock_file_writer(rocks);
                    return;
                // change red count and display it
                case 3:
                    green = green + incrementValue;
                    if (green < 0)
                        green = 0;
                    GreenCount.Text = green.ToString();
                    GreenCountShadow.Text = green.ToString();
                    rocks = rock_file_reader();
                    rocks[3] = green.ToString();
                    rock_file_writer(rocks);
                    return;
                // change red count and display it
                case 4:
                    blue = blue + incrementValue;
                    if (blue < 0)
                        blue = 0;
                    BlueCount.Text = blue.ToString();
                    BlueCountShadow.Text = blue.ToString();
                    rocks = rock_file_reader();
                    rocks[4] = blue.ToString();
                    rock_file_writer(rocks);
                    return;
                // change red count and display it
                case 5:
                    purple = purple + incrementValue;
                    if (purple == -1)
                        purple = 0;
                    PurpleCount.Text = purple.ToString();
                    PurpleCountShadow.Text = purple.ToString();
                    rocks = rock_file_reader();
                    rocks[5] = purple.ToString();
                    rock_file_writer(rocks);
                    return;
            }
        }

        private List<Buttons> knownToBeDown = new List<Buttons>();
        public void Button(Buttons b)
        {
            // if a is pressed
            if (currentState.IsButtonDown(b))
            {
                if (knownToBeDown.Contains(b))
                    return;
                knownToBeDown.Add(b);
                Console.WriteLine("" + b.ToString() + " pressed");

                switch (b)
                {
                    case Buttons.Start:
                        if (end == false)
                        {
                            Console.WriteLine("Start timer");
                            // display that timer is ticking
                            TimerStatusTextBlock.Text = "Ticking...";
                            // display in blue
                            TimerStatusTextBlock.Foreground = Brushes.Blue;
                        }
                        break;
                    case Buttons.Back:
                        if (end == false)
                        {
                            Console.WriteLine("Pause timer");
                            // display that timer is paused
                            TimerStatusTextBlock.Text = "Paused";
                            // display in yellow
                            TimerStatusTextBlock.Foreground = Brushes.Yellow;
                        }
                        break;
                    case Buttons.DPadDown: DPadDownButton(); break;
                    case Buttons.DPadUp: DPadUpButton(); break;
                    case Buttons.DPadLeft: DPadLeftButton(); break;
                    case Buttons.DPadRight: DPadRightButton(); break;
                    case Buttons.RightStick: RightStickButton(); break;
                }
            }
            else
                knownToBeDown.Remove(b);
        }

        // right stick function; reset timer
        public void RightStickButton()
        {
            // if right stick is clicked/pressed
            if (currentState.Buttons.RightStick == ButtonState.Pressed)
            {
                // if timer is at the end
                if (end == true)
                {
                    Console.WriteLine("Right stick pressed");
                    // reset hours to one
                    hours = 1;
                    // reset minutes to 0
                    minutes = 0;
                    // reset seconds to 0
                    seconds = 0;
                    // reset near to false
                    near = false;
                    // reset end to false
                    end = false;
                    // display timer info in green 
                    TimerStatusTextBlock.Foreground = Brushes.Green;
                    // display timer info as ready
                    TimerStatusTextBlock.Text = "Ready";
                    // display timer in black
                    TimerTextBlock.Foreground = Brushes.Black;
                }
            }
        }
        
        // dpad up functions
        public void DPadUpButton()
        {
            // if up is pressed
            if (currentState.DPad.Up == ButtonState.Pressed)
            {
                    // set the increment value to 1
                    incrementValue = 1;
                    // call the function
                    rockIncrement();
            }
        }

        // dpad left functions
        public void DPadLeftButton()
        {
            // if dpad left is pressed
            if (currentState.DPad.Left == ButtonState.Pressed)
            {
                    rockRing--;

                    if (rockRing < 0)
                        rockRing = 5;

                    ringSwitch();
            }
        }

        // dpad right functions
        public void DPadRightButton()
        {
            // if dpad right is pressed
            if (currentState.DPad.Right == ButtonState.Pressed)
            {
                    rockRing++;
                    rockRing = rockRing % 6;
                    ringSwitch();
             
            }
        }

        // dpad down functions
        public void DPadDownButton()
        {
            // if dpad down is pressed
            if (currentState.DPad.Down == ButtonState.Pressed)
            {
                    // set the increment value to -1
                    incrementValue = -1;
                    // call this function
                    rockIncrement();
                
            }
        }
        private void Rock_Restorer_Click(object sender, RoutedEventArgs e)
        {
            string[] rocks;
            rocks = rock_file_reader();
            string rock;
            for (int i=0; i<6; i++)
            {
                rock = rocks[i];
                int aux = int.Parse(rock);
                if (i == 0) { red = aux; RedCount.Text = red.ToString(); RedCountShadow.Text = red.ToString(); }
                if (i == 1) { orange = aux; OrangeCount.Text = orange.ToString(); OrangeCountShadow.Text = orange.ToString(); }
                if (i == 2) { yellow = aux; YellowCount.Text = yellow.ToString(); YellowCountShadow.Text = yellow.ToString(); }
                if (i == 3) { green = aux; GreenCount.Text = green.ToString(); GreenCountShadow.Text = green.ToString(); }
                if (i == 4) { blue = aux; BlueCount.Text = blue.ToString(); BlueCountShadow.Text = blue.ToString(); }
                if (i == 5) { purple = aux; PurpleCount.Text = purple.ToString(); PurpleCountShadow.Text = purple.ToString(); }
            }
            Rock_Restorer.IsEnabled = false;
            Rock_Restorer.Visibility = Visibility.Hidden;
            rocks_restored = true;
        }
        private string[] rock_file_reader()
        {
            StreamReader rreader = new StreamReader(@"rocks.txt");
            string[] rocks = rreader.ReadToEnd().Split(',');
            rreader.Close();            
            return rocks;
        }
        private void rock_file_writer(string[] rocks)
        {
            StreamWriter rwriter = new StreamWriter(@"rocks.txt");
            foreach (string rock in rocks)
            {
                rwriter.Write(rock + ",");
            }
            rwriter.Close();
        }
        
    }

    public class DetectionHelper {
        int cameraNumber;
        Subscriber<imgDataArray> sub;
        MainWindow theWindow;

        public DetectionHelper(NodeHandle node, int cameraNumber, MainWindow w) {
            sub = node.subscribe<imgDataArray>("/camera" + cameraNumber + "/detect", 1000, detectCallback);
            this.cameraNumber = cameraNumber;
            theWindow = w;
        }

        void detectCallback(imgDataArray yes) { Console.WriteLine("dang"); }
    }
}
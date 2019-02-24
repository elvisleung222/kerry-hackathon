using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using DJI.WindowsSDK;
using Windows.UI.Popups;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;
using Windows.Graphics.Imaging;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Controller;
using DJI.WindowsSDK.Components;

namespace WSDKTest
{
    public sealed partial class MainPage : Page
    {
        private DJIVideoParser.Parser videoParser;
        public WriteableBitmap VideoSource;

        //Worker task (thread) for reading barcode
        //As reading barcode is computationally expensive
        private Task readerWorker = null;
        private ISet<string> readed = new HashSet<string>();

        private object bufLock = new object();
        //these properties are guarded by bufLock
        private int width, height;
        private byte[] decodedDataBuf;

        private readonly PathController controller;

        private readonly Stopwatch _timer;
        private static readonly object fLock;

        public MainPage()
        {
            this.InitializeComponent();
            //Listen for registration success
            Focus(FocusState.Keyboard);
            DJISDKManager.Instance.SDKRegistrationStateChanged += async (state, result) =>
            {
                if (state != SDKRegistrationState.Succeeded)
                {
                    var md = new MessageDialog(result.ToString());
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async ()=> await md.ShowAsync());
                    return;
                }
                //wait till initialization finish
                //use a large enough time and hope for the best
                await Task.Delay(1000);
                videoParser = new DJIVideoParser.Parser();
                videoParser.Initialize();
                videoParser.SetVideoDataCallack(0, 0, ReceiveDecodedData);
                DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated += OnVideoPush;

                await DJISDKManager.Instance.ComponentManager.GetFlightAssistantHandler(0, 0).SetObstacleAvoidanceEnabledAsync(new BoolMsg() { value = false });


                await Task.Delay(5000);
                GimbalResetCommandMsg resetMsg = new GimbalResetCommandMsg() { value = GimbalResetCommand.UNKNOWN };

                await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0).ResetGimbalAsync(resetMsg);
            };
            DJISDKManager.Instance.RegisterApp("489330473bb6ed62837e4f05");

            //Call once to initialize fields
            SetControlState(false);

            this._timer = new Stopwatch();
            this._timer.Start();

            var flightAssistant = DJISDKManager.Instance?.ComponentManager.GetFlightAssistantHandler(0, 0);
            flightAssistant?.SetVisionAssistedPositioningEnabledAsync(new BoolMsg {value = true});
            flightAssistant?.SetObstacleAvoidanceEnabledAsync(new BoolMsg { value = true });

            var flightController = DJISDKManager.Instance?.ComponentManager.GetFlightControllerHandler(0, 0);
            var attitudeUpdateTimer = new Stopwatch();
            attitudeUpdateTimer.Start();
            flightController.AttitudeChanged += (sender, value) =>
            {
                _attitudeInterval = attitudeUpdateTimer.Elapsed;
                attitudeUpdateTimer.Restart();
                if (value.HasValue)
                {
                    _attitude = new Attitude
                    {
                        pitch = value.Value.pitch,
                        roll = value.Value.roll,
                        yaw = value.Value.yaw
                    };
                }

                UpdateYawCommand();

                UpdateAction();

                UpdateDebugPrintBoxAsync();
            };

            var heightUpdateTimer = new Stopwatch();
            heightUpdateTimer.Start();
            flightController.AltitudeChanged += (sender, value) =>
            {
                _heightInterval = heightUpdateTimer.Elapsed;
                heightUpdateTimer.Restart();
                if (value.HasValue)
                {
                    _height = value.Value.value;
                }

                UpdateHeightCommand();

                UpdateAction();

                UpdateDebugPrintBoxAsync();
            };

        }

        private void SetControlState(bool state)
        {
            _controlActive = state;

            if (!state)
            {
                _heightTarget = _height ?? 1.5;
                _attitudeTarget = _attitude ?? default;
            }
        }

        //Cached drone states
        private bool _controlActive = false;

        private Attitude? _attitude = null;
        private Attitude _attitudeTarget = new Attitude
        {
            pitch = 0,
            roll = 0,
            yaw = 0
        };
        private TimeSpan? _attitudeInterval = null;
        private double? _height = null;
        private double _heightTarget = 0;
        private TimeSpan? _heightInterval = null;

        private class Control
        {
            public double pitch = 0;
            public double roll = 0;
            public double yaw = 0;
            public double thrust = 0;
        }

        private readonly Control _command = new Control();


        private async void UpdateDebugPrintBoxAsync()
        {
            string spinSymbol = default;

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, 
                () =>
                {
                    spinSymbol = spinSymbol == "*" ? "-" : "*";
                    Textbox.Text = $"{spinSymbol}" + Environment.NewLine +
                                   $"{FormatTimerPrefix(_attitudeInterval)} @ " + Environment.NewLine +
                                   $"Pitch={FormatNullable(_attitude?.pitch)}@{FormatNullable(_command.pitch)}#{FormatNullable(_attitudeTarget.pitch)}; " + Environment.NewLine +
                                   $"Roll={FormatNullable(_attitude?.roll)}@{FormatNullable(_command.roll)}#{FormatNullable(_attitudeTarget.roll)}; " + Environment.NewLine +
                                   $"Yaw={FormatNullable(_attitude?.yaw)}@{FormatNullable(_command.yaw)}#{FormatNullable(_attitudeTarget.yaw)}" + Environment.NewLine + 
                                   $"{FormatTimerPrefix(_heightInterval)} @ " + Environment.NewLine +
                                   $"Height={FormatNullable(_height)}@{FormatNullable(_command.thrust)}#{FormatNullable(_heightTarget)}" + Environment.NewLine +
                                   $"  Average code size={(_sizes.Any() ? _sizes.Average() : 0)}";
                });
        }

        private string FormatTimerPrefix(TimeSpan? time)
        {
            var ms = time?.Milliseconds;
            return $"{(ms / 1000).ToString() ?? "null"}.{(ms % 1000).ToString() ?? "null"} s";
        }

        private string FormatNullable(object obj)
        {
            return obj != null ? obj.ToString() : "null";
        }

        private void UpdateHeightCommand()
        {
            const double kP = 0.4;

            if (_height.HasValue)
            {
                var output = kP * (_heightTarget - _height.Value);
                _command.thrust = output;
            }
        }

        private void UpdateYawCommand()
        {
            const double kP = 0.05;

            if (_attitude.HasValue)
            {
                var output = kP * ((_attitudeTarget.yaw - _attitude.Value.yaw + 180) % 360 - 180);
                _command.yaw = output;
            }
        }

        private void UpdatePitchCommand()
        {
            const double kP = 0.05;
            const double refCodeSize = 45;

            if (_sizes.Any())
            {
                var output = kP * (refCodeSize - _sizes.Average());
                _command.pitch = output;
            }
            else
            {
                _command.pitch = 0;
            }
        }

        private double Limit(double input)
        {
            const double limit = 0.5;

            return input > limit ? limit : input < -limit ? -limit : input;
        }

        private void UpdateAction()
        {
            if (_controlActive)
            {
                var thrust = Limit(_command.thrust);
                var pitch = Limit(_command.pitch);
                var roll = Limit(_command.roll);
                var yaw = Limit(_command.yaw);

                DJISDKManager.Instance?.VirtualRemoteController.UpdateJoystickValue((float) thrust, (float) yaw, (float) pitch, (float) roll);
            }
        }





        void OnVideoPush(VideoFeed sender, [ReadOnlyArray] ref byte[] bytes)
        {
            videoParser.PushVideoData(0, 0, bytes, bytes.Length);
        }

        private List<double> _sizes = new List<double>();
        private List<Result> _codes = new List<Result>();
        private int? _currLocCode = null;
        private int? _prevLocCode = null;

        void createWorker()
        {
            //create worker thread for reading barcode
            readerWorker = new Task(async () =>
            {
                //use stopwatch to time the execution, and execute the reading process repeatedly
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var reader = new QRCodeMultiReader();               
                SoftwareBitmap bitmap;
                HybridBinarizer binarizer;
                while (true)
                {
                    try
                    {
                        lock(bufLock)
                        {
                            bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
                            bitmap.CopyFromBuffer(decodedDataBuf.AsBuffer());
                        }
                    }
                    catch
                    {
                        //the size maybe incorrect due to unknown reason
                        await Task.Delay(10);
                        continue;
                    }
                    var source = new SoftwareBitmapLuminanceSource(bitmap);
                    binarizer = new HybridBinarizer(source);
                    var results = reader.decodeMultiple(new BinaryBitmap(binarizer));
                    if (results != null && results.Length > 0)
                    {
                        await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            var frame = this._timer.ElapsedMilliseconds / 100;
                            var toAdd = new List<string>();
                            var sizes = new List<double>();

                            foreach (var result in results)
                            {
                                var pos = (X: result.ResultPoints[0].X, Y: result.ResultPoints[0].Y);
                                var code = result.Text;
                                //this.Textbox.Text += $"{code} -> ({pos.X}, {pos.Y})" + Environment.NewLine;

                                toAdd.Add($"{frame}, {code}, {pos.X}, {pos.Y}");

                                var dists = new List<double>();
                                if (result.ResultPoints.Length == 3)
                                {
                                    var p0 = result.ResultPoints[0];
                                    var p1 = result.ResultPoints[1];
                                    var p2 = result.ResultPoints[2];
                                    dists.Add(Math.Sqrt(Math.Pow(p0.X - p1.X, 2) + Math.Pow(p0.Y - p1.Y, 2)));
                                    dists.Add(Math.Sqrt(Math.Pow(p0.X - p2.X, 2) + Math.Pow(p0.Y - p2.Y, 2)));
                                    dists.Add(Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2)));
                                    sizes.Add(dists.Average());
                                }
                            }

                            if (sizes.Any())
                            {
                                _sizes = sizes;
                                _codes = results.ToList();
                                if (_codes.Any())
                                {
                                    _prevLocCode = _currLocCode;
                                    var locCodes = _codes
                                        .Where(x => x.Text.StartsWith("LB"))
                                        .Select(x =>
                                            Int32.TryParse(x.Text.Replace("LB", ""), out var id) ? id : (int?) null)
                                        .Where(x => x.HasValue)
                                        .ToList();
                                    if (locCodes.Any())
                                    {
                                        _currLocCode = locCodes.Cast<int>().Max();
                                    }
                                }

                                UpdatePitchCommand();

                                UpdateAction();

                                //Textbox.Text = $"Average code size is: {_sizes.Average()}" + Environment.NewLine;
                            }

                            lock (toAdd)
                            {
                                //using (var fs = File.Open(@"qrcode.csv", FileMode.Append))
                                {
                                    //using (var sw = new StreamWriter(fs))
                                    {
                                        //toAdd.ForEach(sw.WriteLine);
                                    }
                                }
                            }
                        });
                    }
                    watch.Stop();
                    int elapsed = (int)watch.ElapsedMilliseconds;
                    //run at max 5Hz
                    await Task.Delay(Math.Max(0, 200 - elapsed));
                }
            });
        }

        async void ReceiveDecodedData(byte[] data, int width, int height)
        {
            //basically copied from the sample code
            lock (bufLock)
            {
                //lock when updating decoded buffer, as this is run in async
                //some operation in this function might overlap, so operations involving buffer, width and height must be locked
                if (decodedDataBuf == null)
                {
                    decodedDataBuf = data;
                }
                else
                {
                    if (data.Length != decodedDataBuf.Length)
                    {
                        Array.Resize(ref decodedDataBuf, data.Length);
                    }
                    data.CopyTo(decodedDataBuf.AsBuffer());
                    this.width = width;
                    this.height = height;
                }
            }
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                //dispatch to UI thread to do UI update (image)
                //WriteableBitmap is exclusive to UI thread
                if (VideoSource == null || VideoSource.PixelWidth != width || VideoSource.PixelHeight !=  height)
                {
                    VideoSource = new WriteableBitmap((int)width, (int)height);
                    fpvImage.Source = VideoSource;
                    //Start barcode reader worker after the first frame is received
                    if (readerWorker == null)
                    {
                        createWorker();
                        readerWorker.Start();
                    }
                }
                lock (bufLock)
                {
                    //copy buffer to the bitmap and draw the region we will read on to notify the users
                    decodedDataBuf.AsBuffer().CopyTo(VideoSource.PixelBuffer);
                }
                //Invalidate cache and trigger redraw
                VideoSource.Invalidate();
            });
        }

        private void Stop_Button_Click(object sender, RoutedEventArgs e)
        {
            var throttle = 0;
            var roll = 0;
            var pitch = 0;
            var yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private float throttle = 0;
        private float roll = 0;
        private float pitch = 0;
        private float yaw = 0;

        private async void Grid_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.W:
                case Windows.System.VirtualKey.S:
                {
                    throttle = 0;
                    break;
                }
                case Windows.System.VirtualKey.A:
                case Windows.System.VirtualKey.D:
                {
                    yaw = 0;
                    break;
                }
                case Windows.System.VirtualKey.I:
                case Windows.System.VirtualKey.K:
                {
                    pitch = 0;
                    break;
                }
                case Windows.System.VirtualKey.J:
                case Windows.System.VirtualKey.L:
                {
                    roll = 0;
                    break;
                }
                case Windows.System.VirtualKey.G:
                {
                    var res = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartTakeoffAsync();
                    break;
                }
                case Windows.System.VirtualKey.H:
                {
                    var res = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartAutoLandingAsync();
                    break;
                }
                
            }

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private async void take_off_button_click(object sender, RoutedEventArgs e)
        {
            await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartTakeoffAsync();
        }

        private async void land_button_click(object sender, RoutedEventArgs e)
        {
            await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartAutoLandingAsync();
        }
        

        private bool _faceFront = true;
        private Task running;

        private static class Map
        {
            private static int[][][] _mapFront =
            {
                new[]
                {
                    new[]
                    {
                        103315, 103314, 103313, 103312, 103311, 102310, 102309, 102308, 102307, 102306, 101305, 101304,
                        101303, 101302, 101301
                    },
                    new[]
                    {
                        103215, 103214, 103213, 103212, 103211, 102210, 102209, 102208, 102207, 102206, 101205, 101204,
                        101203, 101202, 101201
                    },
                    new[]
                    {
                        103115, 103114, 103113, 103112, 103111, 102110, 102109, 102108, 102107, 102106, 101105, 101104,
                        101103, 101102, 101101
                    }
                },
                new[]
                {
                    new[]
                    {
                        201301, 201302, 201303, 201304, 201305, 202306, 202307, 202308, 202309, 202310, 203311, 203312,
                        203313, 203314, 203315
                    },
                    new[]
                    {
                        201201, 201202, 201203, 201204, 201205, 202206, 202207, 202208, 202209, 202210, 203211, 203212,
                        203213, 203214, 203215
                    },
                    new[]
                    {
                        201101, 201102, 201103, 201104, 201105, 202106, 202107, 202108, 202109, 202110, 203111, 203112,
                        203113, 203114, 203115
                    }
                }
            };

            public static (int id, (int Side, int Col, int Row)? Pos) GetPositionById(int id)
            {
                for (var side = 0; side < _mapFront.Length; side++)
                {
                    for (var row = 0; row < _mapFront[side].Length; row++)
                    {
                        for (var cell = 0; cell < _mapFront[side][row].Length; cell++)
                        {
                            if (_mapFront[side][row][cell] == id)
                            {
                                return (id, (side, cell, row));
                            }
                        }
                    }
                }

                return (id, null);
            }
        }

        private async void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.N:
                {
                    SetControlState(false);
                    break;
                }
                case Windows.System.VirtualKey.M:
                {
                    SetControlState(true);
                    //TODO: Wait until can view first qr code
                    await Task.Run(() =>
                    {
                        var ids = _codes
                            .Select(x => Int32.TryParse(x.Text.Replace("LB", ""), out var id) ? id : (int?) null)
                            .Where(x => x.HasValue)
                            .ToList();

                        if (ids.Any())
                        {
                            var codePoss = ids
                                .Cast<int>()
                                .Select(Map.GetPositionById)
                                .Where(x => x.Pos.HasValue)
                                .ToList();

                            if (codePoss.Any())
                            {
                                if (!_prevLocCode.HasValue)
                                {
                                    //If not previously detected code set current minimum as previous
                                    _prevLocCode = codePoss.Min(x => x.id);
                                }

                                foreach (var codePos in codePoss.Where(x => x.id != _prevLocCode))
                                {
                                    codePos.Pos
                                }

                                //TODO: Calculate distance between 
                            }
                        }
                    });
                    break;
                }
                case Windows.System.VirtualKey.W:
                {
                    throttle += 0.02f;
                    if (throttle > 0.5f)
                        throttle = 0.5f;
                    break;
                }
                case Windows.System.VirtualKey.S:
                {
                    throttle -= 0.02f;
                    if (throttle < -0.5f)
                        throttle = -0.5f;
                    break;
                }
                case Windows.System.VirtualKey.A:
                {
                    yaw -= 0.05f;
                    if (yaw > 0.5f)
                        yaw = 0.5f;
                    break;
                }
                case Windows.System.VirtualKey.D:
                {
                    yaw += 0.05f;
                    if (yaw < -0.5f)
                        yaw = -0.5f;
                    break;
                }
                case Windows.System.VirtualKey.I:
                {
                    pitch += 0.05f;
                    if (pitch > 0.5)
                        pitch = 0.5f;
                    break;
                }
                case Windows.System.VirtualKey.K:
                {
                    pitch -= 0.05f;
                    if (pitch < -0.5f)
                        pitch = -0.5f;
                    break;
                }
                case Windows.System.VirtualKey.J:
                {
                    roll -= 0.05f;
                    if (roll < -0.5f)
                        roll = -0.5f;
                    break;
                }
                case Windows.System.VirtualKey.L:
                {
                    roll += 0.05f;
                    if (roll > 0.5)
                        roll = 0.5f;
                    break;
                }
                case Windows.System.VirtualKey.Number0:
                {
                    GimbalAngleRotation rotation = new GimbalAngleRotation()
                    {
                        mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                        pitch = 45,
                        roll = 45,
                        yaw = 45,
                        pitchIgnored = false,
                        yawIgnored = false,
                        rollIgnored = false,
                        duration = 0.5
                    };

                    System.Diagnostics.Debug.Write("pitch = 45\n");

                    // Defined somewhere else
                    var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

                    //angle
                    var gimbalRotation = new GimbalAngleRotation();
                    gimbalRotation.pitch = 45;
                    gimbalRotation.pitchIgnored = false;
                    gimbalRotation.duration = 5;
                    await gimbalHandler.RotateByAngleAsync(gimbalRotation);

                    //Speed
                    var gimbalRotation_speed = new GimbalSpeedRotation();
                    gimbalRotation_speed.pitch = 10;
                    await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);

                    await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0,0).RotateByAngleAsync(rotation);

                    break;
                }
                case Windows.System.VirtualKey.P:
                {
                    GimbalAngleRotation rotation = new GimbalAngleRotation()
                    {
                        mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                        pitch = 45,
                        roll = 45,
                        yaw = 45,
                        pitchIgnored = false,
                        yawIgnored = false,
                        rollIgnored = false,
                        duration = 0.5
                    };

                    System.Diagnostics.Debug.Write("pitch = 45\n");

                    // Defined somewhere else
                    var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

                    //Speed
                    var gimbalRotation_speed = new GimbalSpeedRotation();
                    gimbalRotation_speed.pitch = -10;
                    await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);

                    await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0,0).RotateByAngleAsync(rotation);

                    break;
                }
            }

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch(Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }
    }
}

﻿using Autofac;
using BililiveRecorder.Core;
using BililiveRecorder.FlvProcessor;
using NLog;
using System;
using System.Collections.ObjectModel;
using System.Deployment.Application;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace BililiveRecorder.WPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private const int MAX_LOG_ROW = 25;
        private const string LAST_WORK_DIR_FILE = "lastworkdir";

        private IContainer Container { get; set; }
        private ILifetimeScope RootScope { get; set; }

        public Recorder Recorder { get; set; }
        public ObservableCollection<string> Logs { get; set; } =
            new ObservableCollection<string>()
            {
                "当前版本：" + BuildInfo.Version,
                "注：按鼠标右键复制日志",
                "网站： https://rec.danmuji.org",
            };

        public static void AddLog(string message) => _AddLog?.Invoke(message);
        private static Action<string> _AddLog;

        public MainWindow()
        {
            Title += "   版本号: " + BuildInfo.Version + "  " + BuildInfo.HeadShaShort;
            _AddLog = (message) => Log.Dispatcher.Invoke(() => { Logs.Add(message); while (Logs.Count > MAX_LOG_ROW) { Logs.RemoveAt(0); } });

            var builder = new ContainerBuilder();
            builder.RegisterModule<FlvProcessorModule>();
            builder.RegisterModule<CoreModule>();
            Container = builder.Build();
            RootScope = Container.BeginLifetimeScope("recorder_root");

            Recorder = RootScope.Resolve<Recorder>();

            InitializeComponent();

            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            string workdir = string.Empty;
            try
            {
                workdir = File.ReadAllText(LAST_WORK_DIR_FILE);
            }
            catch (Exception) { }
            var wdw = new WorkDirectoryWindow()
            {
                Owner = this,
                WorkPath = workdir,
            };
            if (wdw.ShowDialog() == true)
            {
                workdir = wdw.WorkPath;
            }
            else
            {
                Environment.Exit(-1);
                return;
            }

            if (!Recorder.Initialize(workdir))
            {
                MessageBox.Show("初始化错误", "录播姬", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(-2);
                return;
            }

            Task.Run(() => CheckVersion());
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _AddLog = null;
            Recorder.Shutdown();
            try
            {
                File.WriteAllText(LAST_WORK_DIR_FILE, Recorder.Config.WorkDirectory);
            }
            catch (Exception) { }
        }

        #region - 更新检查 -

        private void CheckVersion()
        {
            UpdateBar.MainButtonClick += UpdateBar_MainButtonClick;
            // 定时每6小时检查一次
            Repeat.Interval(TimeSpan.FromHours(6), () => UpdateBar.Dispatcher.Invoke(() =>
            {
                if (ApplicationDeployment.IsNetworkDeployed && UpdateAction == null)
                {
                    ApplicationDeployment ad = ApplicationDeployment.CurrentDeployment;
                    ad.CheckForUpdateCompleted += Ad_CheckForUpdateCompleted;
                    ad.CheckForUpdateAsync();
                }
            }), new CancellationToken());
        }

        private Action UpdateAction = null;
        private void UpdateBar_MainButtonClick(object sender, RoutedEventArgs e) => UpdateAction?.Invoke();

        private void Ad_CheckForUpdateCompleted(object sender, CheckForUpdateCompletedEventArgs e)
        {
            ApplicationDeployment ad = ApplicationDeployment.CurrentDeployment;
            if (e.Error != null)
            {
                logger.Error(e.Error, "检查版本更新出错");
                return;
            }
            if (e.Cancelled)
            {
                return;
            }

            if (e.UpdateAvailable)
            {
                if (e.IsUpdateRequired)
                {
                    BeginUpdate();
                }
                else
                {
                    UpdateAction = () => BeginUpdate();
                    UpdateBar.Dispatcher.Invoke(() =>
                    {
                        UpdateBar.MainText = string.Format("发现新版本: {0} 大小: {1}KiB", e.AvailableVersion, e.UpdateSizeBytes / 1024);
                        UpdateBar.ButtonText = "下载更新";
                        UpdateBar.Display = true;
                    });
                }
            }
        }

        private void BeginUpdate()
        {
            ApplicationDeployment ad = ApplicationDeployment.CurrentDeployment;
            ad.UpdateCompleted += Ad_UpdateCompleted;
            ad.UpdateProgressChanged += Ad_UpdateProgressChanged;
            ad.UpdateAsync();
            UpdateBar.Dispatcher.Invoke(() =>
            {
                UpdateBar.ProgressText = "0KiB / 0KiB - 0%";
                UpdateBar.Progress = 0;
                UpdateBar.Display = true;
                UpdateBar.ShowProgressBar = true;
            });
        }

        private void Ad_UpdateProgressChanged(object sender, DeploymentProgressChangedEventArgs e)
        {
            UpdateBar.Dispatcher.Invoke(() =>
            {
                var p = (e.BytesTotal == 0) ? 100d : (e.BytesCompleted / (double)e.BytesTotal) * 100d;
                UpdateBar.Progress = p;
                UpdateBar.ProgressText = string.Format("{0}KiB / {1}KiB - {2}%", e.BytesCompleted / 1024, e.BytesTotal / 1024, p.ToString("0.##"));
            });
        }

        private void Ad_UpdateCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            UpdateBar.Dispatcher.Invoke(() =>
            {
                if (e.Cancelled)
                {
                    UpdateBar.Display = false;
                    return;
                }
                if (e.Error != null)
                {
                    UpdateBar.Display = false;
                    logger.Error(e.Error, "下载更新时出现错误");
                    return;
                }

                UpdateAction = () =>
                    {
                        Recorder.Shutdown();
                        System.Windows.Forms.Application.Restart();
                        Application.Current.Shutdown();
                    };
                UpdateBar.MainText = "更新已下载好，要现在重启软件吗？";
                UpdateBar.ButtonText = "重启软件";
                UpdateBar.ShowProgressBar = false;
            });
        }

        #endregion

        private void TextBlock_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                Clipboard.SetText(textBlock.Text);
            }
        }

        /// <summary>
        /// 触发回放剪辑
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Clip_Click(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Task.Run(() => rr.Clip());
        }

        /// <summary>
        /// 启用自动录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EnableAutoRec(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Task.Run(() => rr.Start());
        }

        /// <summary>
        /// 禁用自动录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableAutoRec(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Task.Run(() => rr.Stop());
        }

        /// <summary>
        /// 手动触发尝试录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TriggerRec(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Task.Run(() => rr.StartRecord());
        }

        /// <summary>
        /// 切断当前录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CutRec(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Task.Run(() => rr.StopRecord());
        }

        /// <summary>
        /// 删除当前房间
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RemoveRecRoom(object sender, RoutedEventArgs e)
        {
            var rr = _GetSenderAsRecordedRoom(sender);
            if (rr == null)
            {
                return;
            }

            Recorder.RemoveRoom(rr);
        }

        /// <summary>
        /// 全部直播间启用自动录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EnableAllAutoRec(object sender, RoutedEventArgs e)
        {
            Recorder.Rooms.ToList().ForEach(rr => Task.Run(() => rr.Start()));
        }

        /// <summary>
        /// 全部直播间禁用自动录制
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisableAllAutoRec(object sender, RoutedEventArgs e)
        {
            Recorder.Rooms.ToList().ForEach(rr => Task.Run(() => rr.Stop()));
        }

        /// <summary>
        /// 添加直播间
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AddRoomidButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(AddRoomidTextBox.Text, out int roomid))
            {
                if (roomid > 0)
                {
                    Recorder.AddRoom(roomid);
                }
                else
                {
                    logger.Info("房间号是大于0的数字！");
                }
            }
            else
            {
                logger.Info("房间号是数字！");
            }
            AddRoomidTextBox.Text = string.Empty;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsWindow();
        }

        private void ShowSettingsWindow()
        {
            var sw = new SettingsWindow(this, Recorder.Config);
            if (sw.ShowDialog() == true)
            {
                sw.Config.CopyPropertiesTo(Recorder.Config);
            }
        }

        private IRecordedRoom _GetSenderAsRecordedRoom(object sender) => (sender as Button)?.DataContext as IRecordedRoom;


    }
}

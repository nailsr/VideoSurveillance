using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Security.Credentials.UI;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;
using Windows.Web.Http.Filters;

namespace VSMonitor
{
    public sealed partial class MainPage : Page
    {
        bool isPreviewing = false;
        bool isConnected = false;
        bool isSettingsOpened = false;

        DisplayRequest displayRequest = null;

        HttpClient http = null;

        string sessionID = null;

        string URL = null;
        string LoginName = null;
        string Password = null;

        System.IO.Stream openedChannelStream = null;

        int selectedChannelIndex = -1;
        string selectedChannelURL = null;
        double FPS = 25.0;

        TimeSpan videoCurrentTime;
        TimeSpan videoFrameInterval;

        int unsuccessfullConnectTries = 0;

        public struct Buffer
        {
            public bool Busy;
            public byte[] Data;
            public MediaStreamSample Sample;
        }

        public enum NotifyType
        {
            StatusMessage,
            ErrorMessage
        }

        int BufferSize = 4096;
        int MaxBuffers = 20;
        Buffer[] buffers = null;

        Storyboard ctrlWaitStoryboard = null;
        Grid ctrlWait = new Grid() { Name = "ctrlWait", HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), Visibility = Visibility.Collapsed };
        Ellipse ctrlWaitGears = new Ellipse() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = 100, Height = 100, Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2, StrokeDashArray = { 2, 10 }, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform() { CenterX = 0, CenterY = 0 } };

        SettingsPage settingsPage = new SettingsPage() { Name="settingsPage", Visibility = Visibility.Collapsed };

        public MainPage()
        {
            this.InitializeComponent();

            URL = (string)Windows.Storage.ApplicationData.Current.LocalSettings.Values["URL"];

            try
            {
                var vault = new Windows.Security.Credentials.PasswordVault();
                var credentialList = vault.FindAllByResource(Package.Current.DisplayName);
                if (credentialList.Count > 0)
                {
                    LoginName = credentialList[0].UserName;
                    Password = vault.Retrieve(Package.Current.DisplayName, LoginName).Password;
                }
            }
            catch (Exception)
            { }

            if (string.IsNullOrWhiteSpace(URL)) URL = "https://localhost:7443";

            ApplicationViewTitleBar formattableTitleBar = ApplicationView.GetForCurrentView().TitleBar;
            formattableTitleBar.ButtonBackgroundColor = Colors.Transparent;
            CoreApplicationViewTitleBar coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;

            Color PrimaryColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Color ContrastColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            Color SemiColor = Color.FromArgb(0xFF, 0x7F, 0x7F, 0x7F);

            ApplicationView AppView = ApplicationView.GetForCurrentView();

            AppView.TitleBar.ButtonInactiveBackgroundColor = ContrastColor;
            AppView.TitleBar.ButtonInactiveForegroundColor = PrimaryColor;
            AppView.TitleBar.ButtonBackgroundColor = ContrastColor;
            AppView.TitleBar.ButtonForegroundColor = PrimaryColor;

            AppView.TitleBar.ButtonHoverBackgroundColor = PrimaryColor;
            AppView.TitleBar.ButtonHoverForegroundColor = ContrastColor;

            AppView.TitleBar.ButtonPressedBackgroundColor = SemiColor;
            AppView.TitleBar.ButtonPressedForegroundColor = ContrastColor;

            buffers = new Buffer[MaxBuffers];

            ctrlWait.Children.Add(ctrlWaitGears);

            MainFrameContentGrid.Children.Add(ctrlWait);
            Grid.SetRow(ctrlWait, 0);
            Grid.SetColumn(ctrlWait, 0);

            MainFrameContentGrid.Children.Add(settingsPage);
            Grid.SetRow(settingsPage, 0);
            Grid.SetColumn(settingsPage, 0);

            (settingsPage.FindName("btnConnect") as Button).Click += (s, e) =>
            {
                CameraList_SelectionChanged(s, new SelectionChangedEventArgs(new List<object>(), new List<object>()));
            };

            (settingsPage.FindName("btnRemoveCredentials") as Button).Click += (s, e) =>
            {
                try
                {
                    var vault = new Windows.Security.Credentials.PasswordVault();

                    var credentialList = vault.FindAllByResource(Package.Current.DisplayName);
                    while (credentialList.Count > 0)
                    {
                        vault.Remove(credentialList[0]);
                        credentialList = vault.FindAllByResource(URL);
                    }
                }
                catch (Exception)
                { }

                LoginName = null;
                Password = null;

                (settingsPage.FindName("btnRemoveCredentials") as Button).Visibility = Visibility.Collapsed;

                CameraList.Items.Clear();

                CameraList_SelectionChanged(s, new SelectionChangedEventArgs(new List<object>(), new List<object>()));
            };

            Application.Current.Suspending += Application_Suspending;

            this.Unloaded += async (s, e) =>
            {
                if (isConnected)
                {
                    if (http != null && !string.IsNullOrWhiteSpace(sessionID))
                    {
                        await http.GetStringAsync("/logout");
                    }

                    http.Dispose();

                    http = null;

                    sessionID = null;
                }
            };
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopPreview();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            NotifyUser(String.Empty, NotifyType.StatusMessage);

            if (!isConnected)
            {
                await Connect();
            }
            else
            {
                await StartPreview();
            }

            base.OnNavigatedTo(e);
        }

        /// <summary>
        /// Display a message to the user.
        /// This method may be called from any thread.
        /// </summary>
        /// <param name="strMessage"></param>
        /// <param name="type"></param>
        public void NotifyUser(string strMessage, NotifyType type)
        {
            // If called from the UI thread, then update immediately.
            // Otherwise, schedule a task on the UI thread to perform the update.
            if (Dispatcher.HasThreadAccess)
            {
                UpdateStatus(strMessage, type);
            }
            else
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateStatus(strMessage, type));
            }
        }

        private void UpdateStatus(string strMessage, NotifyType type)
        {
            switch (type)
            {
                case NotifyType.StatusMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Green);
                    break;
                case NotifyType.ErrorMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Red);
                    break;
            }

            StatusBlock.Text = strMessage;

            // Collapse the StatusBlock if it has no text to conserve real estate.
            StatusBorder.Visibility = (StatusBlock.Text != String.Empty) ? Visibility.Visible : Visibility.Collapsed;
            if (StatusBlock.Text != String.Empty)
            {
                StatusBorder.Visibility = Visibility.Visible;
                StatusPanel.Visibility = Visibility.Visible;
            }
            else
            {
                StatusBorder.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Clear the status block when navigating scenarios.
            NotifyUser(String.Empty, NotifyType.StatusMessage);

            if(isSettingsOpened)
            {
                URL = (settingsPage.FindName("URL") as TextBox).Text;

                Windows.Storage.ApplicationData.Current.LocalSettings.Values["URL"] = URL;

                var sb = new Storyboard();

                DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
                {
                    BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                    KeyFrames = {
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 1 },
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,500), Value = 0 }
                    }
                };
                Storyboard.SetTarget(anim0, settingsPage);
                Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

                sb.Children.Add(anim0);

                DoubleAnimationUsingKeyFrames anim1 = new DoubleAnimationUsingKeyFrames()
                {
                    BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                    KeyFrames = {
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 1 }
                    }
                };
                Storyboard.SetTarget(anim1, ctrlWait);
                Storyboard.SetTargetProperty(anim1, "(UIElement.Opacity)");

                sb.Children.Add(anim1);

                ObjectAnimationUsingKeyFrames anim2 = new ObjectAnimationUsingKeyFrames()
                {
                    KeyFrames = {
                    new DiscreteObjectKeyFrame(){KeyTime=new TimeSpan(0, 0, 0, 0, 500), Value=Visibility.Collapsed}
                }
                };
                Storyboard.SetTarget(anim2, settingsPage);
                Storyboard.SetTargetProperty(anim2, "(FrameworkElement.Visibility)");

                sb.Children.Add(anim2);


                ctrlWait.Opacity = 0;
                ctrlWait.Visibility = Visibility.Visible;

                sb.Begin();

                await Task.Delay(500);

                ActionList.SelectedIndex = -1;

                isSettingsOpened = false;

                selectedChannelIndex = CameraList.SelectedIndex;

                await Connect();

                return;
            }
            else
            {
                var sb = new Storyboard();

                DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
                {
                    BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                    KeyFrames = {
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 1 },
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,500), Value = 0 }
                    }
                };
                Storyboard.SetTarget(anim0, PreviewControl);
                Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

                sb.Children.Add(anim0);

                if (ctrlWait.Visibility == Visibility.Collapsed)
                {
                    DoubleAnimationUsingKeyFrames anim1 = new DoubleAnimationUsingKeyFrames()
                    {
                        BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                        KeyFrames = {
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 1 }
                        }
                    };
                    Storyboard.SetTarget(anim1, ctrlWait);
                    Storyboard.SetTargetProperty(anim1, "(UIElement.Opacity)");

                    sb.Children.Add(anim1);

                    ctrlWait.Opacity = 0;
                    ctrlWait.Visibility = Visibility.Visible;
                }

                sb.Begin();

                await Task.Delay(500);
            }

            if (Window.Current.Bounds.Width < 640)
            {
                Splitter.IsPaneOpen = false;
            }

            if (isConnected)
            {
                if (CameraList.SelectedItem == null)
                {
                    if (isPreviewing) StopPreview();

                    selectedChannelIndex = -1;
                    selectedChannelURL = null;

                    await HideWait();
                }
                else
                {
                    dynamic stream = (CameraList.SelectedItem as ListBoxItem).Tag;

                    selectedChannelIndex = CameraList.SelectedIndex;
                    selectedChannelURL = stream.URL;

                    if (stream.FPS > 0) FPS = stream.FPS; else FPS = 25.0;

                    await StartPreview();
                }
            }
            else
            {
                await Connect();
            }
        }

        private async void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ActionList.SelectedIndex == -1) return;
            {
                var sb = new Storyboard();

                DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
                {
                    BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                    KeyFrames = {
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 1 },
                        new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,500), Value = 0 }
                    }
                };
                Storyboard.SetTarget(anim0, PreviewControl);
                Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

                sb.Children.Add(anim0);

                if (ctrlWait.Visibility == Visibility.Collapsed)
                {
                    DoubleAnimationUsingKeyFrames anim1 = new DoubleAnimationUsingKeyFrames()
                    {
                        BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                        KeyFrames = {
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 1 }
                        }
                    };
                    Storyboard.SetTarget(anim1, ctrlWait);
                    Storyboard.SetTargetProperty(anim1, "(UIElement.Opacity)");

                    sb.Children.Add(anim1);

                    ctrlWait.Opacity = 0;
                    ctrlWait.Visibility = Visibility.Visible;
                }

                sb.Begin();

                CameraList.SelectedIndex = -1;

                await Task.Delay(500);
            }

            if (isPreviewing) StopPreview();

            switch (ActionList.SelectedIndex)
            {
                case 0:
                    settingsPage.Opacity = 0;
                    settingsPage.Visibility = Visibility.Visible;

                    (settingsPage.FindName("URL") as TextBox).Text = URL;

                    (settingsPage.FindName("btnConnect") as FrameworkElement).Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;

                    (settingsPage.FindName("btnRemoveCredentials") as FrameworkElement).Visibility = Visibility.Collapsed;

                    try
                    {
                        var vault = new Windows.Security.Credentials.PasswordVault();
                        var credentialList = vault.FindAllByResource(Package.Current.DisplayName);
                        if (credentialList.Count > 0)
                        {
                            (settingsPage.FindName("btnRemoveCredentials") as FrameworkElement).Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception)
                    { }

                    {
                        var sb = new Storyboard();

                        DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
                        {
                            BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                            KeyFrames = {
                                new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                                new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,500), Value = 1 }
                            }
                        };
                        Storyboard.SetTarget(anim0, settingsPage);
                        Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

                        sb.Children.Add(anim0);

                        sb.Begin();
                    }

                    isSettingsOpened = true;

                    break;
            }
        }

        private void NavigationToggleButton_Click(object sender, RoutedEventArgs e)
        {
            Splitter.IsPaneOpen = !Splitter.IsPaneOpen;
        }

        private async Task Connect()
        {
            try
            {
                isConnected = false;

                await ShowWait();

                if (unsuccessfullConnectTries > 10) throw new Exception("Could not connect to server!");

                var handler = new HttpClientHandler();
                
                if(!string.IsNullOrWhiteSpace(LoginName))
                {
                    handler.Credentials = new System.Net.NetworkCredential() { UserName = LoginName, Password = Password };
                    handler.PreAuthenticate = true;
                }
                
                var filter = new HttpBaseProtocolFilter();
                
                filter.ClearAuthenticationCache();
                filter.AllowUI = true;

                http = new HttpClient(handler) { BaseAddress = new Uri(URL) };

                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

                string json = null;

                try
                {
                    json = await http.GetStringAsync("/streams");
                }
                catch(HttpRequestException ex)
                {
                    if(ex.InnerException!=null)   
                    {
                        NotifyUser(ex.InnerException.Message, NotifyType.ErrorMessage);

                        ActionList.SelectedIndex = 0;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(LoginName)) NotifyUser("Authenticated failed", NotifyType.ErrorMessage);

                        CredentialPickerOptions credPickerOptions = new CredentialPickerOptions();
                        credPickerOptions.Message = "Authentication required";
                        credPickerOptions.Caption = "Credentials";
                        credPickerOptions.TargetName = URL;
                        credPickerOptions.AlwaysDisplayDialog = true;
                        credPickerOptions.AuthenticationProtocol = AuthenticationProtocol.Basic;

                        var credPickerResults = await Windows.Security.Credentials.UI.CredentialPicker.PickAsync(credPickerOptions);

                        if (string.IsNullOrWhiteSpace(credPickerResults.CredentialUserName)) throw new Exception("Authentication required");

                        LoginName = credPickerResults.CredentialUserName;
                        Password = credPickerResults.CredentialPassword;

                        if(credPickerResults.CredentialSaveOption == CredentialSaveOption.Selected)
                        {
                            var vault = new Windows.Security.Credentials.PasswordVault();

                            try
                            {
                                var credentialList = vault.FindAllByResource(Package.Current.DisplayName);
                                while (credentialList.Count > 0)
                                {
                                    vault.Remove(credentialList[0]);
                                    credentialList = vault.FindAllByResource(URL);
                                }
                            }
                            catch (Exception)
                            { }

                            vault.Add(new Windows.Security.Credentials.PasswordCredential(Package.Current.DisplayName, LoginName, Password));
                        }

                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await Connect());
                    }

                    return;
                }

                var result = Windows.Data.Json.JsonObject.Parse(json);

                sessionID = result["SessionID"].GetString();

                var streams = result["Streams"].GetArray();

                CameraList.Items.Clear();

                for (uint i = 0; i < streams.Count; i++)
                {
                    var stream = streams.GetObjectAt(i);

                    if (string.IsNullOrWhiteSpace(stream["Format"].GetString()) || string.Compare(stream["Format"].GetString(), "RAW", true) == 0)
                    {
                        var url = stream["URL"].GetString();

                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            CameraList.Items.Add(new ListBoxItem() { Content = new TextBlock() { Text = stream["Description"].GetString() }, Tag = new { URL = url, FPS = stream["FPS"].GetNumber() } });
                        }
                    }
                }

                if (selectedChannelIndex < 0 || selectedChannelIndex >= CameraList.Items.Count) selectedChannelIndex = CameraList.Items.Count == 0 ? -1 : 0;

                isConnected = true;

                CameraList.SelectedIndex = selectedChannelIndex;
            }
            catch (Exception ex)
            {
                NotifyUser(ex.Message, NotifyType.ErrorMessage);

                await HideWait();

                ActionList.SelectedIndex = 0;
            }
        }

        private async Task StartPreview()
        {
            try
            {
                if(ctrlWait.Visibility == Visibility.Collapsed) await ShowWait();

                if (isPreviewing) StopPreview();

                if (selectedChannelIndex < 0 || string.IsNullOrWhiteSpace(selectedChannelURL)) return;

                if (http == null || string.IsNullOrWhiteSpace(sessionID))
                {
                    NotifyUser("Not connected", NotifyType.ErrorMessage);
                    return;
                }

                var mss = new MediaStreamSource(new VideoStreamDescriptor(VideoEncodingProperties.CreateH264())) { CanSeek = false };

                try
                {
                    openedChannelStream = await http.GetStreamAsync(selectedChannelURL);
                }
                catch(Exception)
                {
                    unsuccessfullConnectTries++;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await StartPreview());
                    return;
                }

                unsuccessfullConnectTries = 0;

                for (int i = 0; i < MaxBuffers; i++)
                {
                    if (buffers[i].Busy) buffers[i].Busy = false;
                }

                videoCurrentTime = TimeSpan.Zero;
                videoFrameInterval = TimeSpan.FromMilliseconds(1000.0 / FPS);

                mss.Starting += (s, e) =>
                {
                    e.Request.SetActualStartPosition(videoCurrentTime);
                };

                int lag = 0;

                mss.SampleRendered += (s, e) =>
                {
                    lag = (int)e.SampleLag.TotalMilliseconds;
                };

                mss.SampleRequested += (s, e) =>
                {
                    if (openedChannelStream != null)
                    {
                        try
                        {
                            if (lag < 0 || lag > 3000)
                            {
                                //return;
                            }

                            int bufferNo = -1;

                            uint progress = 0;

                            while (bufferNo < 0)
                            {
                                for (int i = 0; i < MaxBuffers; i++)
                                {
                                    if (!buffers[i].Busy)
                                    {
                                        buffers[i].Busy = true;
                                        bufferNo = i;
                                        break;
                                    }
                                }

                                if (bufferNo >= 0) break;

                                e.Request.ReportSampleProgress(progress++); if (progress > 100) return;

                                using (var waitHandle = new System.Threading.ManualResetEventSlim(initialState: false)) waitHandle.Wait(TimeSpan.FromMilliseconds(500));
                            }

                            if (buffers[bufferNo].Data == null) buffers[bufferNo].Data = new byte[BufferSize];

                            if (buffers[bufferNo].Data == null) throw new Exception("Could not allocate memory for a buffer!");

                            int occupiedBuffers = 0;

                            for (int i = 0; i < MaxBuffers; i++)
                            {
                                if (buffers[i].Busy) occupiedBuffers++;
                            }

                            if (openedChannelStream == null) return;

                            var n = openedChannelStream.Read(buffers[bufferNo].Data, 0, BufferSize);

                            if (n <= 0)
                            {
                                return;
                            }

                            var sample = MediaStreamSample.CreateFromBuffer(buffers[bufferNo].Data.AsBuffer(0, n), videoCurrentTime);

                            if (sample == null) throw new Exception("Could not create a sample from buffer!");

                            videoCurrentTime.Add(videoFrameInterval);

                            sample.Processed += (s1, e1) =>
                            {
                                try
                                {
                                    for (int i = 0; i < MaxBuffers; i++)
                                    {
                                        if (buffers[i].Sample == s1)
                                        {
                                            buffers[i].Busy = false;
                                            buffers[i].Sample = null;
                                            break;
                                        }
                                    }
                                }
                                catch (Exception)
                                { }
                            };

                            buffers[bufferNo].Sample = sample;

                            e.Request.Sample = sample;

                            //NotifyUser("Buffers used " + occupiedBuffers.ToString() + ". Lag is " + lag.ToString("000000"), NotifyType.StatusMessage);
                        }
                        catch (Exception ex)
                        {
                            NotifyUser("Connection lost: " + ex.Message, NotifyType.ErrorMessage);
                            s.NotifyError(MediaStreamSourceErrorStatus.ConnectionToServerLost);
                        }
                    }
                };

                mss.Paused += async (s, e) =>
                {
                    NotifyUser("Connection lost. Trying reconnect...", NotifyType.ErrorMessage);

                    unsuccessfullConnectTries++;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await StartPreview());
                };

                displayRequest = new DisplayRequest();

                if (displayRequest != null) displayRequest.RequestActive();
                DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;

                PreviewControl.SetMediaStreamSource(mss);

                PreviewControl.Play();

                {
                    var sb = new Storyboard();

                    DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
                    {
                        BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                        KeyFrames = {
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,1,0), Value = 1 }
                        }
                    };
                    Storyboard.SetTarget(anim0, PreviewControl);
                    Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

                    sb.Children.Add(anim0);

                    DoubleAnimationUsingKeyFrames anim1 = new DoubleAnimationUsingKeyFrames()
                    {
                        BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                        KeyFrames = {
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 1 },
                            new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 0 }
                        }
                    };
                    Storyboard.SetTarget(anim1, ctrlWait);
                    Storyboard.SetTargetProperty(anim1, "(UIElement.Opacity)");

                    sb.Children.Add(anim1);

                    ObjectAnimationUsingKeyFrames anim2 = new ObjectAnimationUsingKeyFrames()
                    {
                        KeyFrames = {
                            new DiscreteObjectKeyFrame(){KeyTime=new TimeSpan(0, 0, 0, 1, 0), Value=Visibility.Collapsed}
                        }
                    };
                    Storyboard.SetTarget(anim2, ctrlWait);
                    Storyboard.SetTargetProperty(anim2, "(FrameworkElement.Visibility)");

                    sb.Children.Add(anim2);

                    sb.Begin();
                }

                isPreviewing = true;
            }
            catch (UnauthorizedAccessException)
            {
                // This will be thrown if the user denied access to the camera in privacy settings
                NotifyUser("The app was denied access to the camera", NotifyType.ErrorMessage);
            }
            catch (Exception ex)
            {
                // This will be thrown if the user denied access to the camera in privacy settings
                NotifyUser(ex.Message, NotifyType.ErrorMessage);
            }
        }

        private void StopPreview()
        {
            if (isPreviewing)
            {
                try
                {
                    PreviewControl.Stop();
                    PreviewControl.Source = null;

                    if (openedChannelStream != null)
                    {
                        openedChannelStream.Dispose();
                        openedChannelStream = null;
                    }

                    if (displayRequest != null) displayRequest.RequestRelease();
                }
                catch (Exception)
                {
                }

                isPreviewing = false;

                return;
            }
        }

        private void Application_Suspending(object sender, SuspendingEventArgs e)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                StopPreview();
                deferral.Complete();
            }
        }

        IAsyncAction HideWait()
        {
            return Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { HideWaitInternal(); });
        }

        IAsyncAction ShowWait()
        {
            return Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { ShowWaitInternal(); });
        }

        void ShowWaitInternal()
        {
            if (ctrlWaitStoryboard == null) ctrlWaitStoryboard = new Storyboard(); else { ctrlWaitStoryboard.Stop(); ctrlWaitStoryboard.Children.Clear(); }

            DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
            {
                BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                KeyFrames = {
                    new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 0 },
                    new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 1 }
                }
            };
            Storyboard.SetTarget(anim0, ctrlWait);
            Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

            ctrlWaitStoryboard.Children.Add(anim0);

            DoubleAnimation anim1 = new DoubleAnimation() { BeginTime = TimeSpan.Zero, RepeatBehavior = RepeatBehavior.Forever, From = 0, To = 359, Duration = TimeSpan.FromSeconds(3) };

            Storyboard.SetTarget(anim1, ctrlWaitGears.RenderTransform);
            Storyboard.SetTargetProperty(anim1, "(RotateTransform.Angle)");

            ctrlWaitStoryboard.Children.Add(anim1);

            DoubleAnimationUsingKeyFrames anim2 = new DoubleAnimationUsingKeyFrames() { KeyFrames = { new DiscreteDoubleKeyFrame() { KeyTime = TimeSpan.Zero, Value = 0 }, new LinearDoubleKeyFrame() { KeyTime = TimeSpan.FromSeconds(1), Value = 1 } } };

            Storyboard.SetTarget(anim2, ctrlWaitGears);
            Storyboard.SetTargetProperty(anim2, "(UIElement.Opacity)");

            ctrlWaitStoryboard.Children.Add(anim2);

            ctrlWait.Opacity = 0;
            ctrlWait.Visibility = Visibility.Visible;

            ctrlWaitStoryboard.Begin();
        }

        void HideWaitInternal()
        {
            var sb = new Storyboard();

            DoubleAnimationUsingKeyFrames anim0 = new DoubleAnimationUsingKeyFrames()
            {
                BeginTime = new TimeSpan(0, 0, 0, 0, 0),
                KeyFrames = {
                    new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,0), Value = 1 },
                    new EasingDoubleKeyFrame(){ KeyTime = new TimeSpan(0,0,0,0,300), Value = 0 }
                }
            };
            Storyboard.SetTarget(anim0, ctrlWait);
            Storyboard.SetTargetProperty(anim0, "(UIElement.Opacity)");

            sb.Children.Add(anim0);

            ObjectAnimationUsingKeyFrames anim1 = new ObjectAnimationUsingKeyFrames()
            {
                KeyFrames = {
                    new DiscreteObjectKeyFrame(){KeyTime=new TimeSpan(0, 0, 0, 0, 300), Value=Visibility.Collapsed}
                }
            };
            Storyboard.SetTarget(anim1, ctrlWait);
            Storyboard.SetTargetProperty(anim1, "(FrameworkElement.Visibility)");

            sb.Children.Add(anim1);

            sb.Begin();
        }

    }
}

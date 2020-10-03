﻿using System;
using System.IO;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Security;
using System.Windows.Forms;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using static SuRGeoNix.Flyleaf.MediaRouter;
using Timer = System.Timers.Timer;

namespace SuRGeoNix.Flyleaf.Controls
{
    public partial class FlyleafPlayer : UserControl
    {
        public  MediaRouter player;

        bool    designMode = (LicenseManager.UsageMode == LicenseUsageMode.Designtime);

        Form    form;
        Timer   timer;
        long    userFullActivity;
        long    userActivity;
        int     seekSum, seekStep;
        bool    seeking;

        static int      cursorHideTimes = 0;
        static object   cursorHideTimesLocker = new object();

        #region Initializing / Disposing
        private void OnLoad(object sender, EventArgs e) { 
            if (designMode) return;

            Thread t = new Thread(() =>
            {
                Thread.Sleep(70);

                thisInitSize = Size;

                thisLastPos     = Location;
                thisLastSize    = Size;
                thisParent      = Parent;

                if (!isWPF && ParentForm != null)
                {
                    form            = ParentForm;
                    formLastSize    = form.Size;
                    formInitSize    = form.Size;
                    formLastPos     = form.Location;
                    formBorderStyle = form.FormBorderStyle;
                }

                if (ParentForm != null) NormalScreen();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); 

            if (player.renderer.HookControl != null) return;

            t = new Thread(() =>
            {
                Thread.Sleep(70);
                Initialize();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); 
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (designMode) return;

            if (config.hookForm._Enabled && config.hookForm.AllowResize) Application.RemoveMessageFilter(this.mouseMessageFilter);

            player?.Close(); 
        }
        protected override void OnPaintBackground(PaintEventArgs e) { if (designMode) player.Render(); }

        //protected override void OnPaint(PaintEventArgs e) { }

        public FlyleafPlayer()
        {
            InitializeComponent();
            
            if (!designMode) tblBar.Visible = false;

            player  = new MediaRouter(1);
            config  = new Settings(player);

            Load                    += OnLoad;
            ParentChanged           += OnParentChanged;
            config.PropertyChanged   = SettingsChanged;
        }
        private void SettingsChanged()
        {
            if (config.bar.Play)    tblBar.EnableColumn(0); else tblBar.DisableColumn(0);
            if (config.bar.Playlist)tblBar.EnableColumn(2); else tblBar.DisableColumn(2);
            if (config.bar.Mute)    tblBar.EnableColumn(3); else tblBar.DisableColumn(3);
            if (config.bar.Volume)  tblBar.EnableColumn(4); else tblBar.DisableColumn(4);

            if (config.AllowDrop)   AllowDrop = true;

            if (designMode)
            {
                player.Activity = config._PreviewMode;
                if (Controls.Contains(tblBar) && !ShouldVisible(config._PreviewMode, config.bar._Visibility) ) { Controls.Remove(tblBar); } else if (!Controls.Contains(tblBar) && ShouldVisible(config._PreviewMode, config.bar._Visibility)) { Controls.Add(tblBar); }
            }
        }
        private void OnParentChanged(object sender, EventArgs e)
        {
            //if (Parent != null) thisParent = Parent;
            if (player.renderer.HookControl != null) return;
            
            if (designMode)
            {
                form = ParentForm;
                player.InitHandle(config.hookForm.HookHandle ? form.Handle : Handle, designMode);
                player.renderer.CreateSample(config.SampleFrame);

                return;
            }

            if (ParentForm != null)
            {
                ParentForm.Load += (o, e1) => { OnLoad(o,e1); };
                Initialize();
            }
        }
        public void Initialize()
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Initialize())); return; }
            
            thisInitSize = Size;
            thisLastPos         = Location;
            thisLastSize        = Size;
            thisParent          = Parent;

            if (!isWPF && ParentForm != null)
            {
                form            = ParentForm;
                formLastSize    = form.Size;
                formInitSize    = form.Size;
                formLastPos     = form.Location;
                formBorderStyle = form.FormBorderStyle;
            }

            screen = Screen.FromControl(this).Bounds;
            player.InitHandle(config.hookForm.HookHandle ? form.Handle : Handle);
            player.OpenFinishedClbk          = OpenFinished;
            player.StatusChanged            += Player_StatusChanged;
            player.audioPlayer.VolumeChanged+= OnVolumeChanged;

            if (config.bar._Visibility != VisibilityMode.Never)
            {
                tblBar.volBar.SetValue(player.Volume);

                HandlersBar();

                if (config.hookForm._Enabled)
                    FormHandlersChangeFocus();
                else
                    HandlersChangeFocus();
            }
            else
            {
                tblBar.btnPlaylist.Click        += BtnPlaylist_Click;
                tblBar.seekBar.ValueChanged     += SeekBar_ValueChanged;
                tblBar.volBar.ValueChanged      += VolBar_ValueChanged;
            }

            if (config.EmbeddedList)
            {
                lstMediaFiles.MouseDoubleClick  += lstMediaFiles_MouseClickDbl;
                lstMediaFiles.KeyPress          += lstMediaFiles_KeyPress;
            }

            if (config.AllowDrop)       HandlersDragDrop();

            if (config.keys._Enabled)   HandlersKeyboard();

            if (config.AllowFullScreen)
                MouseDoubleClick += (o, e) =>   {   FullScreenToggle(); };

            if (config.AllowTorrents)
            {
                player.OpenTorrentSuccessClbk   =   OpenTorrentSuccess;
                player.MediaFilesClbk           =   MediaFilesReceived;
            }

            if (config.hookForm._Enabled)
            {
                if (config.hookForm.HookKeys)       FormHandlersKeyboard();
                if (config.hookForm.AllowResize)    FormHandlersMouse();
                if (config.AllowDrop)               FormHandlersDragDrop();
                if (config.AllowFullScreen)         form.MouseDoubleClick += (o, e) => { FullScreenToggle(); };
            }
            
            if (!config.hookForm.AllowResize || !config.hookForm._Enabled) MouseMove += FlyLeaf_MouseMove;

            if (config.bar._Visibility != VisibilityMode.Never) tblBar.Visible = true;

            if (form != null && form.Created) NormalScreen();

            userActivity                = DateTime.UtcNow.Ticks;
            userFullActivity            = userActivity;

            timer                       = new Timer(100);
            timer.SynchronizingObject   = this;
            timer.AutoReset             = true;
            timer.Elapsed               += Timer_Elapsed;

            timer.Start();
            player.Render();
        }
        #endregion

        #region Activity
        private void Player_StatusChanged(object source, StatusChangedArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Player_StatusChanged(source, e))); return; }

            if (e.status == MediaRouter.Status.PLAYING) 
                { tblBar.btnPlay.BackgroundImage = Properties.Resources.Pause;  tblBar.btnPlay.Text  = " "; }
            else
                { tblBar.btnPlay.BackgroundImage = Properties.Resources.Play;   tblBar.btnPlay.Text  = "";  }
        }
        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            ActivityMode curActivityMode = CurActivityMode();

            if (player.Activity != curActivityMode)
            {
                Console.WriteLine("Activity changed to -> " + curActivityMode.ToString());

                player.Activity = curActivityMode;

                if      (curActivityMode == ActivityMode.FullActive)    GoFullActive();
                else if (curActivityMode == ActivityMode.Active)        GoActive();
                else if (curActivityMode == ActivityMode.Idle)          GoIdle();
            }

            if (resizing) tblBar.Visible = false;

            if (!player.isPlaying) return;
            
            if ( !seeking)
            {
                if (tblBar.seekBar.Maximum == 1) return; // TEMP... thorws exception just after open
                int barValue = (int)(player.CurTime / 10000000);
                barValue = barValue > (int)tblBar.seekBar.Maximum ? (int)tblBar.seekBar.Maximum : barValue;
                barValue = barValue < (int)tblBar.seekBar.Minimum ? (int)tblBar.seekBar.Minimum : barValue;

                tblBar.seekBar.SetValue(barValue);
            }
        }
        private ActivityMode CurActivityMode()
        {
            if ((DateTime.UtcNow.Ticks - userFullActivity   ) / 10000 < config.IdleTimeout) return ActivityMode.FullActive;
            if ((DateTime.UtcNow.Ticks - userActivity       ) / 10000 < config.IdleTimeout) return ActivityMode.Active;

            return ActivityMode.Idle;
        }
        private void GoIdle()
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => GoIdle())); return; }

            player.Activity = ActivityMode.Idle;

            if (config.HideCursor)
            {
                lock (cursorHideTimesLocker)
                {
                    Cursor.Hide();
                    cursorHideTimes++;
                }
            }

            ToggleBarVisibility();
            if (!player.isPlaying) player.Render();
        }
        private void GoActive()
        {
            player.Activity = ActivityMode.Active;
            ToggleBarVisibility();
        }
        private void GoFullActive()
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => GoFullActive())); return; }

            player.Activity = ActivityMode.FullActive;

            if (config.HideCursor)
            {
                lock (cursorHideTimesLocker)
                {
                    for (int i = 0; i < cursorHideTimes; i++) Cursor.Show();
                    cursorHideTimes = 0;
                }
            }

            if (resizing) return;

            ToggleBarVisibility();
            if (!player.isPlaying) player.Render();
            
        }
        private void ToggleBarVisibility()
        {
            if (config.bar._Visibility == VisibilityMode.Never) return;

            bool shouldbe = ShouldVisible(player.Activity, config.bar._Visibility);

            if (!tblBar.Visible && shouldbe)
                tblBar.Visible = true;
            else if (tblBar.Visible && !shouldbe)
                { tblBar.Visible = false; player.Render(); }
        }
        #endregion

        #region Torrent
        public void OpenTorrentSuccess(bool success) { }
        public void MediaFilesReceived(List<string> mediaFiles, List<long> mediaFilesSizes)
        {
            if (!config.EmbeddedList)
            {
                player.SetMediaFile(mediaFiles[0]);
                return;
            }

            if ( lstMediaFiles.InvokeRequired  )
            {
                lstMediaFiles.BeginInvoke(new Action(() => MediaFilesReceived(mediaFiles, mediaFilesSizes)));
                return;
            }

            string selectedFile = "";

            lstMediaFiles.BeginUpdate();
            lstMediaFiles.Items.Clear();
            
            mediaFiles.Sort(new NaturalStringComparer());
            
            for (int i=0; i<mediaFiles.Count; i++)
            {
                string ext = Path.GetExtension(mediaFiles[i]);
                if ( ext == null || ext.Trim() == "") continue;

                if ( movieExts.Contains(ext.Substring(1,ext.Length-1)) )
                {
                    lstMediaFiles.Items.Add(mediaFiles[i]);
                    selectedFile = mediaFiles[i];
                }
            }
            lstMediaFiles.EndUpdate();

            if (lstMediaFiles.Items.Count == 1)
                player.SetMediaFile(selectedFile);
            else
                FixLstMedia(true);
        }
        #endregion

        #region Implementation Main
        List<string> movieExts = new List<string>() { "mp4", "mkv", "mpg", "mpeg" , "mpv", "mp4p", "mpe" , "m2v", "amv" , "asf", "m4v", "3gp", "ogg", "vob", "ts", "rm", "3g2", "f4v", "f4a", "f4p", "f4b", "mts", "m2ts", "gifv", "avi", "mov", "flv", "wmv", "qt", "avchd", "swf"};
        public void Open(string url)
        {
            bool isSubs             = false;

            string ext = url.Substring(url.LastIndexOf(".") + 1);
            if ((new List<string> { "srt", "txt", "sub", "ssa", "ass" }).Contains(ext)) isSubs = true;

            if (isSubs)
            {
                Encoding subsEnc = Subtitles.Detect(url);
                if (subsEnc != Encoding.UTF8) url = Subtitles.Convert(url, subsEnc, Encoding.UTF8);
                if (url == null || url.Trim() == "") return;

                player.OpenSubs(url);
            } 
            else
            {
                player.Open(url);
            }
            
        }
        public void OpenFinished(bool success, string selectedFile)
        {
            if ( InvokeRequired  )
            {
                BeginInvoke(new Action(() => OpenFinished(success, selectedFile)));
                return;
            }

            tblBar.seekBar.SetValue(0);
            tblBar.seekBar.Maximum = (int)(player.Duration / 10000000);

            if (player.isFailed) return;

            if (!player.hasAudio || !player.doAudio) 
                { tblBar.btnMute.Enabled = false; tblBar.volBar.Enabled = false; }
            else
                { tblBar.btnMute.Enabled = true; tblBar.volBar.Enabled = true; }

            FixAspectRatio(true);

            if (config.EmbeddedList && !player.isTorrent)
            {
                lstMediaFiles.Items.Clear();
                lstMediaFiles.Items.Add(selectedFile);
            }

            Play();
        }
        public void Play() { player.Play(); }
        public void MuteUnmute()
        {
            if (!player.hasAudio && !config.hookForm._Enabled) return;

            if (tblBar.btnMute.Text == "")
            {
                tblBar.btnMute.BackgroundImage = Properties.Resources.Mute;
                tblBar.btnMute.Text = " ";
                if (config.hookForm._Enabled) player.Mute2 = true; else player.Mute = true;
            }
            else
            {
                tblBar.btnMute.BackgroundImage = Properties.Resources.Speaker;
                tblBar.btnMute.Text = "";
                if (config.hookForm._Enabled) player.Mute2 = false; else player.Mute = false;
            }
        }
        public void PlayPause()
        {
            if (!player.isReady) return;

            if (tblBar.btnPlay.Text == "")
            {
                tblBar.btnPlay.BackgroundImage = Properties.Resources.Pause;
                tblBar.btnPlay.Text = " ";
                player.Play();
            } 
            else
            {
                tblBar.btnPlay.BackgroundImage = Properties.Resources.Play;
                tblBar.btnPlay.Text = "";
                player.Pause();
            }
        }
        public void VolUp   (int v)
        {
                if (player.Volume + v > 99) player.Volume = 100; else player.Volume += v;
        }
        public void VolDown (int v)
        {
                if (player.Volume  - v < 1) player.Volume = 0; else player.Volume -= v;
        }
        public void OnVolumeChanged(object sender, AudioPlayer.VolumeChangedArgs e)
        { 
            if (InvokeRequired) { BeginInvoke(new Action(() => OnVolumeChanged(sender, e))); return; }

            tblBar.volBar.Value = e.volume;

            if (e.mute)
            {
                tblBar.btnMute.BackgroundImage = Properties.Resources.Mute;
                tblBar.btnMute.Text = " ";
            }
            else
            {
                tblBar.btnMute.BackgroundImage = Properties.Resources.Speaker;
                tblBar.btnMute.Text = "";   
            }
        }
        #endregion

        #region Implementation - Screen
        Rectangle               screen;
        bool                    isFullScreen;
        Size                    thisInitSize;
        Size                    formInitSize;
        Control                 thisParent;
        FormBorderStyle         formBorderStyle;
        Size                    thisLastSize;
        Size                    formLastSize;
        Point                   thisLastPos;
        Point                   formLastPos;
        public void FullScreenToggle()
        {
            if (!config.AllowFullScreen || isWPF) return;

            if (form.InvokeRequired) { form.BeginInvoke(new Action(() => FullScreenToggle())); return; }

            if (form.Width != screen.Width && form.Height != screen.Height)
                FullScreen();
            else
                NormalScreen();
        }
        public void FullScreen()
        {
            if (!config.AllowFullScreen || isWPF) return;

            screen = Screen.FromControl(this).Bounds;

            if (form.Width != screen.Width && form.Height != screen.Height)
            {
                formLastPos         = form.Location;
                formLastSize        = form.Size;
                formBorderStyle     = form.FormBorderStyle;

                if (!config.hookForm._Enabled) form.FormBorderStyle = FormBorderStyle.None;

                form.Location       = screen.Location;
                form.Size           = screen.Size;

                if (!config.hookForm._Enabled)
                {
                    thisLastPos     = Location;
                    thisLastSize    = Size;

                    thisParent      = Parent;
                    Parent          = form;

                    Location        = screen.Location;
                    Size            = form.Size;
                    BringToFront();
                    Focus();
                    player.Render();
                }
                isFullScreen = true;
            }

            if (lstMediaFiles.Visible) FixLstMedia(true);
        }
        public void NormalScreen    (bool fromOpen = false)
        {
            if (!config.AllowFullScreen  || isWPF || form == null) return;

            if (form.InvokeRequired) { form.BeginInvoke(new Action(() => NormalScreen())); return; }

            if (!config.hookForm._Enabled)
            {
                Parent = thisParent;
                form.FormBorderStyle = formBorderStyle;
                Focus();
            }
            
            form.Location   = formLastPos;
            form.Size       = formLastSize;

            Location        = thisLastPos;
            Size            = thisLastSize;

            isFullScreen = false;

            player.Render();
            FixAspectRatio();
        }
        public void FixAspectRatio  (bool fromOpen = false)
        {
            if (isWPF) return;

            if (!config.hookForm._Enabled || !config.hookForm.AutoResize) return;
            if (form.InvokeRequired) { form.BeginInvoke(new Action(() => FixAspectRatio())); return; }

            float AspectRatio = config.video.AspectRatio == ViewPorts.KEEP ? player.DecoderRatio : player.CustomRatio;

            if (fromOpen) 
                if (config.hookForm._Enabled && config.hookForm.HookHandle) form.Size = formInitSize; else form.Size = thisInitSize;

            if (form.Width == screen.Width && form.Height == screen.Height) return;

            if ( form.Width / AspectRatio > form.Height)
                form.Size = new Size((int)(form.Height * AspectRatio), form.Height);
            else
                form.Size = new Size(form.Width, (int)(form.Width / AspectRatio));

            if (lstMediaFiles.Visible) FixLstMedia(true);

            // Should be docked?
            //screenPlay.Size = form.Size;
        }
        #endregion

        #region User Actions
        private void BtnPlay_Click              (object sender, EventArgs e) { PlayPause(); }
        private void BtnMute_Click              (object sender, EventArgs e) { MuteUnmute(); }
        private void BtnPlaylist_Click          (object sender, EventArgs e) { if (!config.EmbeddedList) return; if (lstMediaFiles.Visible) FixLstMedia(false); else FixLstMedia(true); }

        private void SeekBar_ValueChanged       (object sender, EventArgs e)
        {
            if (!player.isReady) return;

            long seektime = (((long)(tblBar.seekBar.Value) * 1000) + 500);
            if (config.bar.SeekOnSlide) player.Seek((int)seektime); else Interlocked.Exchange(ref player.SeekTime, seektime * 10000);
        }
        private void SeekBar_MouseDown          (object sender, MouseEventArgs e)
        {
            seeking = true;
            userFullActivity = DateTime.UtcNow.Ticks;
        }
        private void SeekBar_MouseUp            (object sender, MouseEventArgs e)
        {
            seeking = false;

            if (!config.bar.SeekOnSlide && player.isReady)
            {
                long seektime = (((long)(tblBar.seekBar.Value) * 1000) + 500);
                player.Seek((int)seektime);
            }
        }

        private void VolBar_ValueChanged        (object sender, EventArgs e)
        {
            player.Volume = (int)tblBar.volBar.Value;
        }

        private void FlyLeaf_DragEnter          (object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.All;
            //lastUserActionTicks = DateTime.UtcNow.Ticks;
        }
        private void FlyLeaf_DragDrop           (object sender, DragEventArgs e)
        {
            if ( e.Data.GetDataPresent(DataFormats.FileDrop) )
            {
                Cursor = Cursors.Default;
                string[] filenames = (string[])e.Data.GetData(DataFormats.FileDrop, false);
                if (filenames.Length > 0) Open(filenames[0]);
            }
            else if ( e.Data.GetDataPresent(DataFormats.Text) )
            {
                Cursor = Cursors.Default;
                string url = e.Data.GetData(DataFormats.Text, false).ToString();
                if (url.Length > 0) Open(url);
            }

            form?.Activate();
        }

        private void FlyLeaf_KeyDown            (object sender, KeyEventArgs e)
        {
            if (e.Control)
            {
                switch (e.KeyCode)
                {
                    // AUDIO
                    case Keys.OemOpenBrackets:
                        if (!player.hasAudio) return;

                        player.AudioExternalDelay -= config.keys.AudioDelayStep2 * 10000;

                        break;

                    case Keys.OemCloseBrackets:
                        if (!player.hasAudio) return;

                        player.AudioExternalDelay += config.keys.AudioDelayStep2 * 10000;

                        break;

                    // SUBTITLES
                    case Keys.OemSemicolon:
                        if (!player.hasSubs) return;

                        player.SubsExternalDelay -= config.keys.SubsDelayStep2 * 10000;

                        break;

                    case Keys.OemQuotes:
                        if (!player.hasSubs) return;

                        player.SubsExternalDelay += config.keys.SubsDelayStep2 * 10000;

                        break;

                    case Keys.Up:
                        if (!player.hasSubs) return;

                        player.SubsPosition += config.keys.SubsPosYStep;

                        break;

                    case Keys.Down:
                        if (!player.hasSubs) return;

                        player.SubsPosition -= config.keys.SubsPosYStep;
                        
                        break;

                    case Keys.Right:
                        if (!player.hasSubs) return;

                        player.SubsFontSize += config.keys.SubsFontSizeStep;
                        
                        break;

                    case Keys.Left:
                        if (!player.hasSubs) return;

                        player.SubsFontSize -= config.keys.SubsFontSizeStep;
                        
                        break;
                }

                return;
            }

            decimal seekValue = 0;

            switch (e.KeyCode)
            {
                // SEEK
                case Keys.Right:
                    if (!player.isReady) return;

                    seeking = true;
                    seekStep++;
                    if (seekStep == 1) seekSum = config.keys.SeekStepFirst - config.keys.SeekStep;
                    seekSum += seekStep * config.keys.SeekStep;

                    seekValue = tblBar.seekBar.Value + seekSum / 1000;
                    if (seekValue > tblBar.seekBar.Maximum) seekValue = tblBar.seekBar.Maximum;
                    if (seekValue != tblBar.seekBar.Value) tblBar.seekBar.Value = seekValue;
                    
                    break;

                case Keys.Left:
                    if (!player.isReady) return;

                    seeking = true;
                    seekStep++;
                    if (seekStep == 1) seekSum = -(config.keys.SeekStepFirst - config.keys.SeekStep);
                    seekSum -= seekStep * config.keys.SeekStep;
                    
                    seekValue = tblBar.seekBar.Value + seekSum / 1000;
                    if (seekValue < tblBar.seekBar.Minimum) seekValue = tblBar.seekBar.Minimum;
                    if (seekValue != tblBar.seekBar.Value) tblBar.seekBar.Value = seekValue;

                    break;

                // AUDIO
                case Keys.OemOpenBrackets:
                    if (!player.hasAudio) return;

                    player.AudioExternalDelay -= config.keys.AudioDelayStep * 10000;

                    break;

                case Keys.OemCloseBrackets:
                    if (!player.hasAudio) return;

                    player.AudioExternalDelay += config.keys.AudioDelayStep * 10000;

                    break;

                // SUBTITLES
                case Keys.OemSemicolon:
                    if (!player.hasSubs) return;

                    player.SubsExternalDelay -= config.keys.SubsDelayStep * 10000;

                    break;

                case Keys.OemQuotes:
                    if (!player.hasSubs) return;

                    player.SubsExternalDelay += config.keys.SubsDelayStep * 10000;

                    break;

                // VOLUME
                case Keys.Up:
                    VolUp(config.keys.VolStep);
                    if (config.bar._Visibility != VisibilityMode.Never) tblBar.volBar.SetValue(player.Volume);
                    
                    break;

                case Keys.Down:
                    VolDown(config.keys.VolStep);
                    if (config.bar._Visibility != VisibilityMode.Never) tblBar.volBar.SetValue(player.Volume);

                    break;
            }
        }
        private void FlyLeaf_KeyUp              (object sender, KeyEventArgs e)
        {
            if (e.Control) return;

            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:

                    if (!config.bar.SeekOnSlide && seeking)
                    {
                        long seektime = (((long)(tblBar.seekBar.Value) * 1000) + 500);
                        player.Seek((int)seektime);
                    }

                    seeking     = false;

                    seekSum     = 0;
                    seekStep    = 0;

                    break;
            }
        }
        private void FlyLeaf_KeyPress           (object sender, KeyPressEventArgs e)
        {
            char c = Char.ToLower(e.KeyChar);

            if (c == KeyCodeToUnicode(Keys.F))
            {
                FullScreenToggle();
            }
            else if (c == KeyCodeToUnicode(Keys.H))
            {
                player.HWAccel = !player.HWAccel;
            }
            else if (c == KeyCodeToUnicode(Keys.I))
            {
                userFullActivity = 0;
                userActivity = 0;
                GoIdle();
                return;
            }
            else if (c == KeyCodeToUnicode(Keys.P) || Keys.Space == (Keys) c) { PlayPause(); }
            else if (c == KeyCodeToUnicode(Keys.R))
            {
                if (player.ViewPort == MediaRouter.ViewPorts.KEEP) player.ViewPort = MediaRouter.ViewPorts.FILL;
                else if (player.ViewPort != MediaRouter.ViewPorts.KEEP) player.ViewPort = MediaRouter.ViewPorts.KEEP;
            }
            else if (c == KeyCodeToUnicode(Keys.M)) { MuteUnmute(); }
            else if (c == KeyCodeToUnicode(Keys.S))
            {
                player.doSubs = !player.doSubs;
            }
            else if (c == KeyCodeToUnicode(Keys.A))
            {
                player.doAudio = !player.doAudio;
            }
            else if ((Keys)c == Keys.Escape)
            {
                if (config.EmbeddedList) if (lstMediaFiles.Visible) FixLstMedia(false); else FixLstMedia(true);
            }
            //{
            //    if (picHelp.Visible)
            //        picHelp.Visible = false;

            //    else
            //        MediaFilesToggle();
            //}

            //lastUserActionTicks         = DateTime.UtcNow.Ticks;
            }

        private void FlyLeaf_MouseMove          (object sender, MouseEventArgs e)
        {
            if (displayMMoveLastPos == e.Location) return;
            displayMMoveLastPos = e.Location;

            userFullActivity = DateTime.UtcNow.Ticks;
        }

        private void lstMediaFiles_MouseClickDbl(object sender, MouseEventArgs e)
        {
            if ( !player.isTorrent ) { lstMediaFiles.Visible = false; if (!player.isPlaying) player.Render(); return; }

            if ( lstMediaFiles.SelectedItem == null ) return;

            player.SetMediaFile(lstMediaFiles.SelectedItem.ToString());
            FixLstMedia(false);
            Focus();
        }
        private void lstMediaFiles_KeyPress     (object sender, KeyPressEventArgs e)
        {
            userActivity = DateTime.UtcNow.Ticks;

            if ( e.KeyChar != (char)13 ) { FlyLeaf_KeyPress(sender, e); return; }

            if ( !player.isTorrent ) { FixLstMedia(false); return; }
            if ( lstMediaFiles.SelectedItem == null ) return;

            player.SetMediaFile(lstMediaFiles.SelectedItem.ToString());
            FixLstMedia(false);
            Focus();
        }
        private void lstMediaFiles_MouseMove    (object sender, MouseEventArgs e) { userFullActivity = DateTime.UtcNow.Ticks; }

        private void FixLstMedia(bool visible)
        {
            if (visible)
            {
                lstMediaFiles.Width = Width / 2 + Width / 4;
                lstMediaFiles.Height = Height / 2 + Height / 4;
                lstMediaFiles.Location = new Point(Width / 8, Height / 8);
                lstMediaFiles.Visible = true;
            }
            else
            {
                lstMediaFiles.Visible = false;
            }

            if (!player.isPlaying) player.Render();
        }
        #endregion

        #region Event Handlers

        // KEYBOARD
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            userActivity = DateTime.UtcNow.Ticks;

            if (!config.keys._Enabled) return base.ProcessCmdKey(ref msg, keyData);

            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:

                    if (msg.HWnd == this.Handle) {
                        KeyEventArgs a = new KeyEventArgs(keyData);
                        FlyLeaf_KeyDown(null, a);
                        return true;
                    }

                    break;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }
        public char KeyCodeToUnicode(Keys key)
        {
            byte[] keyboardState = new byte[255];
            bool keyboardStateStatus = GetKeyboardState(keyboardState);

            if (!keyboardStateStatus)
            {
                return '\0';
            }

            uint virtualKeyCode = (uint)key;
            uint scanCode = MapVirtualKey(virtualKeyCode, 0);
            IntPtr inputLocaleIdentifier = GetKeyboardLayout(0);

            StringBuilder result = new StringBuilder();
            ToUnicodeEx(virtualKeyCode, scanCode, keyboardState, result, (int)5, (uint)0, inputLocaleIdentifier);

            return result.ToString()[0];
        }

        [DllImport("user32.dll")]
        static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        // HANDLERS - Flyleaf
        private void HandlersBar()
        {
            tblBar.btnPlay.Click        += BtnPlay_Click;
            tblBar.btnMute.Click        += BtnMute_Click;
            tblBar.btnPlaylist.Click    += BtnPlaylist_Click;
            tblBar.seekBar.ValueChanged += SeekBar_ValueChanged;
            tblBar.volBar.ValueChanged  += VolBar_ValueChanged;
            tblBar.seekBar.MouseDown    += SeekBar_MouseDown;
            tblBar.seekBar.MouseUp      += SeekBar_MouseUp;
        }
        private void HandlersKeyboard()
        {
            KeyDown     += FlyLeaf_KeyDown;
            KeyPress    += FlyLeaf_KeyPress;
            KeyUp       += FlyLeaf_KeyUp;
        }
        private void HandlersDragDrop()
        {
            AllowDrop    = true;
            DragDrop    += FlyLeaf_DragDrop;
            DragEnter   += FlyLeaf_DragEnter;
        }
        private void HandlersChangeFocus()
        {
            tblBar.btnPlay.     GotFocus += (o, e) => { this.ActiveControl = null; };
            tblBar.btnPlaylist. GotFocus += (o, e) => { this.ActiveControl = null; };
            tblBar.seekBar.     GotFocus += (o, e) => { this.ActiveControl = null; };
            tblBar.btnMute.     GotFocus += (o, e) => { this.ActiveControl = null; };
            tblBar.volBar.      GotFocus += (o, e) => { this.ActiveControl = null; };
        }

        // HANDLERS - PARENT FORM - KEYBOARD | DRAG & DROP | GOT FOCUS
        private void FormHandlersKeyboard()
        {
            form.KeyDown    += FlyLeaf_KeyDown;
            form.KeyUp      += FlyLeaf_KeyUp;
            form.KeyPress   += FlyLeaf_KeyPress;            
        }
        private void FormHandlersDragDrop()
        {
            form.AllowDrop   = true;
            form.DragDrop   += FlyLeaf_DragDrop;
            form.DragEnter  += FlyLeaf_DragEnter;
        }
        private void FormHandlersChangeFocus()
        {
            tblBar.btnPlay.     GotFocus += (o, e) => { form.ActiveControl = null; };
            tblBar.btnPlaylist. GotFocus += (o, e) => { form.ActiveControl = null; };
            tblBar.seekBar.     GotFocus += (o, e) => { form.ActiveControl = null; };
            tblBar.btnMute.     GotFocus += (o, e) => { form.ActiveControl = null; };
            tblBar.volBar.      GotFocus += (o, e) => { form.ActiveControl = null; };
        }

        // HANDLERS - PARENT FORM - MOUSE
        const int MIN_FORM_SIZE             = 230;
        const int RESIZE_CURSOR_DISTANCE    = 6;
        MouseHandler mouseMessageFilter;
        Point       displayMMoveLastPos;
        Point       displayMLDownPos;
        int         displayMoveSide;
        int         displayMoveSideCur;
        bool        resizing = false;

        private void FormHandlersMouse()
        {
            mouseMessageFilter = new MouseHandler();
            mouseMessageFilter.TargetForm = form;
            mouseMessageFilter.flyLeaf = this;
            Application.AddMessageFilter(mouseMessageFilter);
        }
        
        private class MouseHandler : IMessageFilter {
            public Form TargetForm { get; set; }
            public FlyleafPlayer flyLeaf;
            public bool PreFilterMessage( ref Message m )
            {
                switch (m.Msg)
                {
                    case 0x0200:
                        flyLeaf.WM_MOUSEMOVE_MK_LBUTTON(new MouseEventArgs(m.WParam == (IntPtr)0x0001 ? MouseButtons.Left : MouseButtons.None, 0, Control.MousePosition.X - TargetForm.Location.X, Control.MousePosition.Y - TargetForm.Location.Y, 0));
                        break;
                    case 0x0201:
                        flyLeaf.WM_LBUTTONDOWN(Control.MousePosition.X - TargetForm.Location.X, Control.MousePosition.Y - TargetForm.Location.Y);
                        break;
                    case 0x0202:
                        flyLeaf.WM_LBUTTONUP();
                        break;
                }

                return false;
            }
        }
        
        private void WM_LBUTTONUP()                { displayMoveSideCur = 0; resizing = false; GoFullActive(); if (lstMediaFiles.Visible) FixLstMedia(true); }
        private void WM_LBUTTONDOWN(int x, int y)  { displayMLDownPos = new Point(x, y); userFullActivity = DateTime.UtcNow.Ticks; }
        private void WM_MOUSEMOVE_MK_LBUTTON(MouseEventArgs e)
        {
            if (displayMMoveLastPos == e.Location) return;
            displayMMoveLastPos = e.Location;

            userFullActivity = DateTime.UtcNow.Ticks;

            if (isFullScreen) return;

            try
            {
                if (e.Button == MouseButtons.Left) // RESIZING or MOVING
                { 
                    if (displayMoveSide != 0 || displayMoveSideCur != 0)
                    {
                        resizing = true;

                        if (displayMoveSideCur == 0) displayMoveSideCur = displayMoveSide;
                            
                        if (config.video.AspectRatio != ViewPorts.FILL)
                        {
                            float AspectRatio = config.video.AspectRatio == ViewPorts.KEEP ? player.DecoderRatio : player.CustomRatio;

                            // RESIZE FORM [KEEP ASPECT RATIO]
                            if (displayMoveSideCur == 1)
                            {
                                int oldHeight = form.Height;

                                if (form.Width - e.X > MIN_FORM_SIZE && form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    if (e.X > e.Y)
                                    {
                                        int oldWidth = form.Width;
                                        form.Height -= e.Y;
                                        form.Width = (int)(form.Height * AspectRatio);
                                        form.Location = new Point(form.Location.X - (form.Width - oldWidth), form.Location.Y - (form.Height - oldHeight));
                                    }
                                    else
                                    {
                                        form.Width -= e.X;
                                        form.Height = (int)(form.Width / AspectRatio);
                                        form.Location = new Point(form.Location.X + e.X, form.Location.Y - (form.Height - oldHeight));
                                    }
                                }
                                else if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y - (form.Height - oldHeight));
                                }
                                else if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    int oldWidth = form.Width;
                                    form.Height -= e.Y;
                                    form.Width = (int)(form.Height * AspectRatio);
                                    form.Location = new Point(form.Location.X - (form.Width - oldWidth), form.Location.Y - (form.Height - oldHeight));
                                }
                            }
                            else if (displayMoveSideCur == 2)
                            {
                                if (e.X > MIN_FORM_SIZE && e.Y > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                }
                                else if (e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                }
                            }
                            else if (displayMoveSideCur == 3)
                            {
                                int oldHeight = form.Height;

                                if (form.Height - e.Y > MIN_FORM_SIZE && e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                    form.Location = new Point(form.Location.X, form.Location.Y + (oldHeight - form.Height));
                                }
                                else if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height -= e.Y;
                                    form.Width = (int)(form.Height * AspectRatio);
                                    form.Location = new Point(form.Location.X, form.Location.Y + (oldHeight - form.Height));
                                }
                                else if (e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                    form.Location = new Point(form.Location.X, form.Location.Y + (oldHeight - form.Height));
                                }
                            }
                            else if (displayMoveSideCur == 4)
                            {
                                if (form.Width - e.X > MIN_FORM_SIZE && e.Y > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                }
                                else if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                }
                            }
                            else if (displayMoveSideCur == 5)
                            {
                                if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                    form.Width -= e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                }
                            }
                            else if (displayMoveSideCur == 6)
                            {
                                if (e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = (int)(form.Width / AspectRatio);
                                }
                            }
                            else if (displayMoveSideCur == 7)
                            {
                                if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Location = new Point(form.Location.X, form.Location.Y + e.Y);
                                    form.Height -= e.Y;
                                    form.Width = (int)(form.Height * AspectRatio);
                                }
                            }
                            else if (displayMoveSideCur == 8)
                            {
                                if (e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height = e.Y;
                                    form.Width = (int)(form.Height * AspectRatio);
                                }
                            }
                        }
                        else
                        {
                            // RESIZE FORM [DONT KEEP ASPECT RATIO]
                            if (displayMoveSideCur == 1)
                            {
                                if (form.Width - e.X > MIN_FORM_SIZE && form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Height -= e.Y;
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y + e.Y);
                                }
                                else if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                }
                                else if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height -= e.Y;
                                    form.Location = new Point(form.Location.X, form.Location.Y + e.Y);
                                }
                            }
                            else if (displayMoveSideCur == 2)
                            {
                                if (e.X > MIN_FORM_SIZE && e.Y > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height = e.Y;
                                }
                                else if (e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                }
                                else if (e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height = e.Y;
                                }
                            }
                            else if (displayMoveSideCur == 3)
                            {
                                if (form.Height - e.Y > MIN_FORM_SIZE && e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                    form.Height -= e.Y;
                                    form.Location = new Point(form.Location.X, form.Location.Y + e.Y);
                                }
                                else if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height -= e.Y;
                                    form.Location = new Point(form.Location.X, form.Location.Y + e.Y);
                                }
                                else if (e.X > MIN_FORM_SIZE)
                                {
                                    form.Width = e.X;
                                }
                            }
                            else if (displayMoveSideCur == 4)
                            {
                                if (form.Width - e.X > MIN_FORM_SIZE && e.Y > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Height = e.Y;
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                }
                                else if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Width -= e.X;
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                }
                                else if (e.Y > MIN_FORM_SIZE)
                                {
                                    form.Height = e.Y;
                                }
                            }
                            else if (displayMoveSideCur == 5)
                            {
                                if (form.Width - e.X > MIN_FORM_SIZE)
                                {
                                    form.Location = new Point(form.Location.X + e.X, form.Location.Y);
                                    form.Width -= e.X;
                                }
                            }
                            else if (displayMoveSideCur == 6)
                            {
                                if (e.X > MIN_FORM_SIZE)
                                    form.Width = e.X;
                            }
                            else if (displayMoveSideCur == 7)
                            {
                                if (form.Height - e.Y > MIN_FORM_SIZE)
                                {
                                    form.Location = new Point(form.Location.X, form.Location.Y + e.Y);
                                    form.Height -= e.Y;
                                }
                            }
                            else if (displayMoveSideCur == 8)
                            {
                                if (e.Y > MIN_FORM_SIZE)
                                    form.Height = e.Y;
                            }
                        }
                    }
                    else
                    {
                        // MOVING FORM WHILE PRESSING LEFT BUTTON
                        if (seeking) return;
                        if (config.bar._Visibility != VisibilityMode.Never && tblBar.Visible && e.Y > tblBar.Location.Y) return;
                        form.Location = new Point(form.Location.X + e.X - displayMLDownPos.X, form.Location.Y + e.Y - displayMLDownPos.Y);
                    }
                }
                else
                {
                    // SHOW PROPER CURSOR AT THE EDGES (FOR RESIZING)
                    if (e.X <= RESIZE_CURSOR_DISTANCE && e.Y <= RESIZE_CURSOR_DISTANCE)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNWSE;
                        displayMoveSide = 1;
                    }
                    else if (e.X + RESIZE_CURSOR_DISTANCE >= form.Width && e.Y + RESIZE_CURSOR_DISTANCE >= form.Height)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNWSE;
                        displayMoveSide = 2;
                    }
                    else if (e.X + RESIZE_CURSOR_DISTANCE >= form.Width && e.Y <= RESIZE_CURSOR_DISTANCE)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNESW;
                        displayMoveSide = 3;
                    }
                    else if (e.X <= RESIZE_CURSOR_DISTANCE && e.Y + RESIZE_CURSOR_DISTANCE >= form.Height)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNESW;
                        displayMoveSide = 4;
                    }
                    else if (e.X <= RESIZE_CURSOR_DISTANCE)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeWE;
                        displayMoveSide = 5;
                    }
                    else if (e.X + RESIZE_CURSOR_DISTANCE >= form.Width)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeWE;
                        displayMoveSide = 6;
                    }
                    else if (e.Y <= RESIZE_CURSOR_DISTANCE)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNS;
                        displayMoveSide = 7;
                    }
                    else if (e.Y + RESIZE_CURSOR_DISTANCE >= form.Height)
                    {
                        if (displayMoveSideCur == 0) form.Cursor = Cursors.SizeNS;
                        displayMoveSide = 8;
                    }
                    else
                    {
                        if (displayMoveSideCur == 0)
                        {
                            form.Cursor = Cursors.Default;
                            displayMoveSide = 0;
                        }

                    }
                }
            } 
            catch (Exception) { }
        }
        #endregion

        #region Properties
        public bool isWPF = false;

        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        [DisplayName("FL Main")]
        public Settings         config                  { get; set; }

        [DisplayName("FL Keys")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Keys    _keys               { get { return config.keys;     } set { config.keys     = value; } }

        [DisplayName("FL Bar")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Bar     _bar                { get { return config.bar;      } set { config.bar      = value; } }

        [DisplayName("FL Single Mode")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.HookForm _form              { get { return config.hookForm; } set { config.hookForm = value; } }

        [DisplayName("FL Audio")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Audio   _audio              { get { return config.audio;    } set { config.audio = value; } }

        [DisplayName("FL Subtitles")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Subtitles _subtitles        { get { return config.subtitles;} set { config.subtitles = value; } }

        [DisplayName("FL Video")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Video   _video              { get { return config.video;    } set { config.video      = value; } }

        [DisplayName("FL Msg Time")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message0 { get { return config.messages[0]; } set { config.messages[0] = value; } }

        [DisplayName("FL Msg HardwareAcceleration")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message1 { get { return config.messages[1]; } set { config.messages[1] = value; } }

        [DisplayName("FL Msg Volume")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message2 { get { return config.messages[2]; } set { config.messages[2] = value; } }

        [DisplayName("FL Msg Mute")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message3 { get { return config.messages[3]; } set { config.messages[3] = value; } }

        [DisplayName("FL Msg Open")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message4 { get { return config.messages[4]; } set { config.messages[4] = value; } }

        [DisplayName("FL Msg Play")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message5 { get { return config.messages[5]; } set { config.messages[5] = value; } }

        [DisplayName("FL Msg Paused")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message6 { get { return config.messages[6]; } set { config.messages[6] = value; } }

        [DisplayName("FL Msg Buffering")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message7 { get { return config.messages[7]; } set { config.messages[7] = value; } }

        [DisplayName("FL Msg Failed")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message8 { get { return config.messages[8]; } set { config.messages[8] = value; } }

        [DisplayName("FL Msg AudioDelay")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message9 { get { return config.messages[9]; } set { config.messages[9] = value; } }

        [DisplayName("FL Msg SubsDelay")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message10 { get { return config.messages[10]; } set { config.messages[10] = value; } }

        [DisplayName("FL Msg SubsHeight")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message11 { get { return config.messages[11]; } set { config.messages[11] = value; } }

        [DisplayName("FL Msg SubsFontSize")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message12 { get { return config.messages[12]; } set { config.messages[12] = value; } }

        [DisplayName("FL Msg Subtitles")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message13 { get { return config.messages[13]; } set { config.messages[13] = value; } }

        [DisplayName("FL Msg TorrentStats")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message14 { get { return config.messages[14]; } set { config.messages[14] = value; } }

        [DisplayName("FL Msg TopLeft")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message15 { get { return config.messages[15]; } set { config.messages[15] = value; } }

        [DisplayName("FL Msg TopLeft2")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message16 { get { return config.messages[16]; } set { config.messages[16] = value; } }

        [DisplayName("FL Msg TopRight")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message17 { get { return config.messages[17]; } set { config.messages[17] = value; } }

        [DisplayName("FL Msg TopRight2")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message18 { get { return config.messages[18]; } set { config.messages[18] = value; } }

        [DisplayName("FL Msg BottomLeft")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message19 { get { return config.messages[19]; } set { config.messages[19] = value; } }

        [DisplayName("FL Msg BottomRight")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Messages _message20 { get { return config.messages[20]; } set { config.messages[20] = value; } }

        [DisplayName("FL Surface TopLeft")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface1 { get { return config.surfaces[0]; } set { config.surfaces[0] = value; } }

        [DisplayName("FL Surface TopRight")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface2 { get { return config.surfaces[1]; } set { config.surfaces[1] = value; } }

        [DisplayName("FL Surface TopLeft 2")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface3 { get { return config.surfaces[2]; } set { config.surfaces[2] = value; } }

        [DisplayName("FL Surface TopRight 2")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface4 { get { return config.surfaces[3]; } set { config.surfaces[3] = value; } }

        [DisplayName("FL Surface BottomLeft")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface5 { get { return config.surfaces[4]; } set { config.surfaces[4] = value; } }

        [DisplayName("FL Surface BottomRight")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface6 { get { return config.surfaces[5]; } set { config.surfaces[5] = value; } }

        [DisplayName("FL Surface BootomCenter")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Content)]
        public Settings.Surfaces _surface7 { get { return config.surfaces[6]; } set { config.surfaces[6] = value; } }

        [DisplayName("FL Surface All")]
        [DesignerSerializationVisibilityAttribute(DesignerSerializationVisibility.Hidden)]
        public Settings.SurfacesAll _surfaceAll { get { return config.surfacesAll; } set {config.surfacesAll = value; } }
        #endregion

        #region Misc
        [SuppressUnmanagedCodeSecurity]
        internal static class SafeNativeMethods
        {
            [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
            public static extern int StrCmpLogicalW(string psz1, string psz2);
        }
        public sealed class NaturalStringComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                return SafeNativeMethods.StrCmpLogicalW(a, b);
            }
        }
        #endregion
    }
}
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Management;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Speech.Synthesis;
using System.Threading;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Net;
using WMPLib;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

/*
 * Triggernometry II: The Triggering
- trigger copy/paste now available
- aura windows no longer appear when alt-tabbing
- aura opacity set to 100 instead of blank (0) when using the guide for placement
- "disable all auras" action
- "unset all variables" action
- special variable ${_ffxivplayer} for retrieving current character name in FFXIV
- removed unnecessary logging regarding triggers with disabled parent folders
*/

namespace Triggernometry
{

    public class RealPlugin
    {

        public enum DebugLevelEnum
        {
            None,
            Error,
            Warning,
            Info,
            Verbose,
            Inherit
        }

        public class CustomTriggerProxy
        {

            public bool Active { get; set; }
            public string ShortRegexString { get; set; }
            public string SoundData { get; set; }
            public int SoundType { get; set; }
            public string TimerName { get; set; }
            public bool Tabbed { get; set; }
            public bool Timer { get; set; }

        }

        public class CustomTriggerCategoryProxy
        {

            public string Category { get; set; }
            public bool RestrictToCategoryZone { get; set; }
            public List<CustomTriggerProxy> Items = new List<CustomTriggerProxy>();

        }

        public class PluginWrapper
        {

            public object pluginObj { get; set; }
            public int state { get; set; }
            public string fileversion { get; set; }
            public string expectedversion { get; set; }

        }

        internal class WindowsUtils
        {

            const uint WM_KEYUP = 0x101;
            const uint WM_KEYDOWN = 0x100;

            public struct WINDOWPLACEMENT
            {
                public int length;
                public int flags;
                public int showCmd;
                public System.Drawing.Point ptMinPosition;
                public System.Drawing.Point ptMaxPosition;
                public System.Drawing.Rectangle rcNormalPosition;
            }

            const int SW_UNKNOWN = -1;
            const UInt32 SW_HIDE = 0;
            const UInt32 SW_SHOWNORMAL = 1;
            const UInt32 SW_NORMAL = 1;
            const UInt32 SW_SHOWMINIMIZED = 2;
            const UInt32 SW_SHOWMAXIMIZED = 3;
            const UInt32 SW_MAXIMIZE = 3;
            const UInt32 SW_SHOWNOACTIVATE = 4;
            const UInt32 SW_SHOW = 5;
            const UInt32 SW_MINIMIZE = 6;
            const UInt32 SW_SHOWMINNOACTIVE = 7;
            const UInt32 SW_SHOWNA = 8;
            const UInt32 SW_RESTORE = 9;

            [DllImport("user32.dll")]
            private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")]
            private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
            [DllImport("user32.dll")]
            private static extern IntPtr GetForegroundWindow();

            public static void SendKeycode(string windowtitle, int keycode)
            {
                SendMessageToWindow(windowtitle, WM_KEYDOWN, keycode, 0);
                SendMessageToWindow(windowtitle, WM_KEYUP, keycode, 0);
            }

            public static void SendMessageToWindow(string windowtitle, uint code, int wparam, int lparam)
            {
                IntPtr hwnd = FindWindow(null, windowtitle);
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr res = SendMessage(hwnd, code, (IntPtr)wparam, (IntPtr)lparam);
                }
            }

            public static bool IsInFocus(string windowtitle)
            {
                IntPtr hwnd = FindWindow(null, windowtitle);
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                wp.showCmd = SW_UNKNOWN;
                if (hwnd != IntPtr.Zero)
                {
                    wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                    if (GetWindowPlacement(hwnd, ref wp) == true)
                    {
                        if (wp.showCmd == SW_SHOWMINIMIZED)
                        {
                            return false;
                        }
                        IntPtr hwnd2 = GetForegroundWindow();
                        return (hwnd == hwnd2);
                    }
                }
                return true;
            }

        }

        private delegate void LogLineProcDelegate(LogEvent le);
        public delegate bool SimpleBoolDelegate();
        public delegate void SimpleVoidDelegate();
        public delegate double SimpleDoubleDelegate();
        public delegate string SimpleStringDelegate();
        public delegate void TtsDelegate(string text);
        public delegate void SoundDelegate(string filename, int volume);
        public delegate List<CustomTriggerCategoryProxy> CustomTriggerDelegate();
        public delegate PluginWrapper InstanceDelegate(string ActPluginName, string ActPluginType);

        internal Scarborough.Scarborough sc;
        private Queue<LogEvent> EventQueue;
        private ManualResetEvent QueueWakeupEvent;
        internal CustomControls.UserInterface ui;
        internal Configuration cfg;
        public string path { get; set; }
        internal WMPLib.WindowsMediaPlayer wmp;
        internal SpeechSynthesizer tts;
        internal Thread ActionQueueThread;
        internal Thread EventQueueThread;
        internal Thread AuraUpdateThread;
        internal List<QueuedAction> ActionQueue;
        internal AutoResetEvent ActionUpdateEvent;
        private ManualResetEvent ExitEvent;
        private TabPage mytp;
        private Int64 curOrdinal;
        private string updateDownloadUrl;
        public string pluginName { get; set; }
        public string pluginPath { get; set; }
        private bool firstevent = true;
        internal bool WMPUnavailable;
        internal Queue<InternalLog> log;
        internal DateTime LastDelayWarning;
        internal object QueueProcessingLock;
        internal bool QueueProcessing;
        internal bool configBroken;
        internal List<Trigger> Triggers;
        internal List<Trigger> ActiveTextTriggers;
        internal List<Trigger> ActiveFFXIVNetworkTriggers;
        internal Dictionary<string, Variable> simplevariables;
        internal Dictionary<string, VariableList> listvariables;
        internal Dictionary<string, Forms.AuraContainerForm> imageauras;
        internal Dictionary<string, Forms.AuraContainerForm> textauras;
        internal bool DisableLogging;
        internal ObsController _obs = null;
        internal CancellationTokenSource cts = null;
        internal object ctslock = new object();
        public Form mainform { get; set; }
        internal int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;

        public SimpleBoolDelegate InCombatHook { get; set; }
        public SimpleBoolDelegate CustomTriggerCheckHook { get; set; }
        public SimpleVoidDelegate EndCombatHook { get; set; }
        public SimpleStringDelegate CurrentZoneHook { get; set; }
        public SimpleStringDelegate ActiveEncounterHook { get; set; }
        public SimpleStringDelegate LastEncounterHook { get; set; }
        public SimpleDoubleDelegate EncounterDurationHook { get; set; }
        public TtsDelegate TtsPlaybackHook { get; set; }
        public SoundDelegate SoundPlaybackHook { get; set; }
        public CustomTriggerDelegate CustomTriggerHook { get; set; }
        public static InstanceDelegate InstanceHook { get; set; }

        private bool _HideAllAuras = false;
        internal bool HideAllAuras
        {
            get
            {
                return _HideAllAuras;
            }
            set
            {
                if (value != _HideAllAuras)
                {
                    _HideAllAuras = value;
                    if (_HideAllAuras == true)
                    {
                        ProcessAuraControl(true);
                    }
                    else
                    {
                        ProcessAuraControl(false);
                    }
                }
            }
        }

        internal delegate void ActionExecutionHook(Context ctx, Action a);

        internal void UpdateAuras(int numTicks)
        {
            lock (imageauras)
            {
                List<string> toRem = new List<string>();
                foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in imageauras)
                {
                    if (kp.Value.UpdateAura(numTicks) == false)
                    {
                        toRem.Add(kp.Key);
                    }
                }
                foreach (string rem in toRem)
                {
                    imageauras.Remove(rem);
                }
            }
            lock (textauras)
            {
                List<string> toRem = new List<string>();
                foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in textauras)
                {
                    if (kp.Value.UpdateAura(numTicks) == false)
                    {
                        toRem.Add(kp.Key);
                    }
                }
                foreach (string rem in toRem)
                {
                    textauras.Remove(rem);
                }
            }
        }

        internal void ImageAuraManagement(Context ctx, Action a)
        {
            if (_usingScarborough == true)
            {
                SbImageAuraManagement(ctx, a);
            }
            else
            {
                LegacyImageAuraManagement(ctx, a);
            }
        }

        internal void TextAuraManagement(Context ctx, Action a)
        {
            if (_usingScarborough == true)
            {
                SbTextAuraManagement(ctx, a);
            }
            else
            {
                LegacyTextAuraManagement(ctx, a);
            }
        }

        internal void SbImageAuraManagement(Context ctx, Action a)
        {
            switch (a.AuraOp)
            {
                case Action.AuraOpEnum.ActivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/actimageaura", "Activating image aura '{0}'", ax));
                        try
                        { 
                            Scarborough.ScarboroughImage si = new Scarborough.ScarboroughImage(sc);
                            si.ImageExpression = a.AuraImage;
                            si.InitXExpression = a.AuraXIniExpression;
                            si.InitYExpression = a.AuraYIniExpression;
                            si.InitWExpression = a.AuraWIniExpression;
                            si.InitHExpression = a.AuraHIniExpression;
                            si.InitOExpression = a.AuraOIniExpression;
                            si.UpdateXExpression = a.AuraXTickExpression;
                            si.UpdateYExpression = a.AuraYTickExpression;
                            si.UpdateWExpression = a.AuraWTickExpression;
                            si.UpdateHExpression = a.AuraHTickExpression;
                            si.UpdateOExpression = a.AuraOTickExpression;
                            si.TTLExpression = a.AuraTTLTickExpression;
                            si.Display = a.AuraImageMode;
                            si.ctx = ctx;
                            sc.Activate(ax, si);
                        }
                        catch (Exception ex)
                        {
                            FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/exactimageaura", "Exception '{0}' when activating image aura '{1}'", ex.Message, ax));
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactimageaura", "Deactivating image aura '{0}'", ax));
                        sc.Deactivate(ax, Scarborough.Scarborough.ItemAction.ItemTypeEnum.Image);
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAllAura:
                    {
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactallimageaura", "Deactivating all image auras"));
                        sc.DeactivateAllImages();
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAuraRegex:
                    {
                        string ax = a.AuraName;
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactimageaurarex", "Deactivating image auras matching '{0}'", ax));
                        sc.DeactivateRegex(ax, Scarborough.Scarborough.ItemAction.ItemTypeEnum.Image);
                    }
                    break;
            }
        } 

        internal void LegacyImageAuraManagement(Context ctx, Action a)
        {
            if (mainform.InvokeRequired == true)
            {
                string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.AuraName);
                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/invokeimageaura", "Invoke required for image aura '{0}'", ax));
                mainform.Invoke(new ActionExecutionHook(ImageAuraManagement), ctx, a);
                return;
            }
            switch (a.AuraOp)
            {
                case Action.AuraOpEnum.ActivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/actimageaura", "Activating image aura '{0}'", ax));
                        Forms.AuraContainerForm acf = null;
                        bool newAura = false;
                        try
                        {
                            lock (ctx.plug.imageauras)
                            {
                                if (ctx.plug.imageauras.ContainsKey(ax) == true)
                                {
                                    acf = ctx.plug.imageauras[ax];
                                }
                                else
                                {
                                    acf = new Forms.AuraContainerForm(Forms.AuraContainerForm.AuraTypeEnum.Image);
                                    acf.plug = this;
                                    acf.AuraName = ax;
                                    newAura = true;                                    
                                }
                                acf.AuraPrepare();
                                acf.ctx = ctx;
                                acf.ImageExpression = a.AuraImage;
                                acf.Display = a.AuraImageMode;
                                acf.Left = acf.EvaluateNumericExpression(ctx, a.AuraXIniExpression);
                                acf.Top = acf.EvaluateNumericExpression(ctx, a.AuraYIniExpression);
                                int i = acf.EvaluateNumericExpression(ctx, a.AuraWIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                acf.Width = i;
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                i = acf.EvaluateNumericExpression(ctx, a.AuraHIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                acf.Height = i;
                                i = acf.EvaluateNumericExpression(ctx, a.AuraOIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                if (i > 100)
                                {
                                    i = 100;
                                }                                
                                acf.PresentableOpacity = i;
                                acf.XExpression = a.AuraXTickExpression;
                                acf.YExpression = a.AuraYTickExpression;
                                acf.WExpression = a.AuraWTickExpression;
                                acf.HExpression = a.AuraHTickExpression;
                                acf.OExpression = a.AuraOTickExpression;
                                acf.TTLExpression = a.AuraTTLTickExpression;
                                acf.AuraActivate();
                                if (newAura == true)
                                {
                                    ctx.plug.imageauras[ax] = acf;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/exactimageaura", "Exception '{0}' when activating image aura '{1}'", ex.Message, ax));
                            if (acf != null)
                            {
                                if (newAura == true)
                                {
                                    acf.Dispose();
                                }
                                else
                                {
                                    acf.AuraDeactivate();
                                }
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactimageaura", "Deactivating image aura '{0}'", ax));
                        lock (ctx.plug.imageauras)
                        {
                            if (ctx.plug.imageauras.ContainsKey(ax) == true)
                            {
                                Forms.AuraContainerForm acf = ctx.plug.imageauras[ax];
                                acf.AuraDeactivate();
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAllAura:
                    {
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactallimageaura", "Deactivating all image auras"));
                        while (true)
                        {
                            lock (ctx.plug.imageauras)
                            {
                                if (ctx.plug.imageauras.Count > 0)
                                {
                                    Forms.AuraContainerForm acf = ctx.plug.imageauras.ElementAt(0).Value;
                                    acf.AuraDeactivate();
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAuraRegex:
                    {
                        Regex rex = new Regex(a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactimageaurarex", "Deactivating image auras matching '{0}'", a.AuraName));
                        List<string> toRem = new List<string>();
                        lock (ctx.plug.imageauras)
                        {
                            toRem.AddRange(from sx in ctx.plug.imageauras where rex.IsMatch(sx.Key) select sx.Key);
                            foreach (string rem in toRem)
                            {
                                Forms.AuraContainerForm acf = ctx.plug.imageauras[rem];
                                acf.AuraDeactivate();
                            }
                        }
                    }
                    break;
            }
        }

        internal void SbTextAuraManagement(Context ctx, Action a)
        {
            if (mainform.InvokeRequired == true)
            {
                string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/invoketextaura", "Invoke required for text aura '{0}'", ax));
                mainform.Invoke(new ActionExecutionHook(TextAuraManagement), ctx, a);
                return;
            }
            switch (a.TextAuraOp)
            {
                case Action.AuraOpEnum.ActivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/acttextaura", "Activating text aura '{0}'", ax));
                        try
                        {
                            Scarborough.ScarboroughText si = new Scarborough.ScarboroughText(sc);
                            si.InitXExpression = a.TextAuraXIniExpression;
                            si.InitYExpression = a.TextAuraYIniExpression;
                            si.InitWExpression = a.TextAuraWIniExpression;
                            si.InitHExpression = a.TextAuraHIniExpression;
                            si.InitOExpression = a.TextAuraOIniExpression;
                            si.UpdateXExpression = a.TextAuraXTickExpression;
                            si.UpdateYExpression = a.TextAuraYTickExpression;
                            si.UpdateWExpression = a.TextAuraWTickExpression;
                            si.UpdateHExpression = a.TextAuraHTickExpression;
                            si.UpdateOExpression = a.TextAuraOTickExpression;
                            si.TTLExpression = a.TextAuraTTLTickExpression;
                            FontStyle fs = FontStyle.Regular;
                            if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Bold) != 0)
                            {
                                fs |= FontStyle.Bold;
                            }
                            if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Italic) != 0)
                            {
                                fs |= FontStyle.Italic;
                            }
                            if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Underline) != 0)
                            {
                                fs |= FontStyle.Underline;
                            }
                            if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Strikeout) != 0)
                            {
                                fs |= FontStyle.Strikeout;
                            }
                            si.TextExpression = a.TextAuraExpression;
                            si.TextAlignment = a.TextAuraAlignment;
                            si.TextColor = a.TextAuraForegroundClInt;
                            si.OutlineColor = a.TextAuraOutlineClInt;
                            si.UseOutline = a._TextAuraUseOutline;
                            si.FontName = a.TextAuraFontName;
                            si.FontSize = a.TextAuraFontSize;
                            si.FontStyle = fs;
                            si.ctx = ctx;
                            si.BackgroundColor = a.TextAuraBackgroundClInt;
                            sc.Activate(ax, si);
                        }
                        catch (Exception ex)
                        {
                            FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/exacttextaura", "Exception '{0}' when activating text aura '{1}'", ex.Message, ax));
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deacttextaura", "Deactivating text aura '{0}'", ax));
                        sc.Deactivate(ax, Scarborough.Scarborough.ItemAction.ItemTypeEnum.Text);
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAllAura:
                    {
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactalltextaura", "Deactivating all text auras"));
                        sc.DeactivateAllText();
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAuraRegex:
                    {
                        string ax = a.AuraName;
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deacttextaurarex", "Deactivating text auras matching '{0}'", ax));
                        sc.DeactivateRegex(ax, Scarborough.Scarborough.ItemAction.ItemTypeEnum.Text);
                    }
                    break;
            }
        }

        internal void LegacyTextAuraManagement(Context ctx, Action a)
        {
            if (mainform.InvokeRequired == true)
            {
                string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/invoketextaura", "Invoke required for text aura '{0}'", ax));
                mainform.Invoke(new ActionExecutionHook(TextAuraManagement), ctx, a);
                return;
            }
            switch (a.TextAuraOp)
            {
                case Action.AuraOpEnum.ActivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/acttextaura", "Activating text aura '{0}'", ax));
                        Forms.AuraContainerForm acf = null;
                        bool newAura = false;
                        try
                        {
                            lock (ctx.plug.textauras)
                            {
                                if (ctx.plug.textauras.ContainsKey(ax) == true)
                                {
                                    acf = ctx.plug.textauras[ax];
                                }
                                else
                                {
                                    acf = new Forms.AuraContainerForm(Forms.AuraContainerForm.AuraTypeEnum.Text);
                                    acf.plug = this;
                                    acf.AuraName = ax;
                                    newAura = true;
                                }
                                acf.AuraPrepare();
                                acf.ctx = ctx;
                                acf.TextExpression = a.TextAuraExpression;
                                acf.TextAlignment = a.TextAuraAlignment;
                                acf.TextColor = a.TextAuraForegroundClInt;
                                acf.OutlineColor = a.TextAuraOutlineClInt;
                                acf.UseOutline = a._TextAuraUseOutline;
                                if (acf.AuraFont != null)
                                {
                                    FontStyle fs = FontStyle.Regular;
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Bold) != 0)
                                    {
                                        fs |= FontStyle.Bold;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Italic) != 0)
                                    {
                                        fs |= FontStyle.Italic;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Underline) != 0)
                                    {
                                        fs |= FontStyle.Underline;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Strikeout) != 0)
                                    {
                                        fs |= FontStyle.Strikeout;
                                    }
                                    Font x = acf.AuraFont;
                                    if (x.Style != fs || x.Size != a.TextAuraFontSize || x.Name != a.TextAuraFontName)
                                    {
                                        x.Dispose();
                                        acf.AuraFont = null;
                                    }
                                }
                                if (acf.AuraFont == null)
                                {                                    
                                    FontStyle fs = FontStyle.Regular;
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Bold) != 0)
                                    {
                                        fs |= FontStyle.Bold;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Italic) != 0)
                                    {
                                        fs |= FontStyle.Italic;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Underline) != 0)
                                    {
                                        fs |= FontStyle.Underline;
                                    }
                                    if ((a.TextAuraEffect & Action.TextAuraEffectEnum.Strikeout) != 0)
                                    {
                                        fs |= FontStyle.Strikeout;
                                    }
                                    float ex = a.TextAuraFontSize;
                                    if (ex < 1)
                                    {
                                        ex = 1;
                                    }                                    
                                    acf.AuraFont = new Font(a.TextAuraFontName, ex, fs, GraphicsUnit.Point);
                                }
                                acf.BackgroundColor = a.TextAuraBackgroundClInt;
                                if (acf.BackgroundColor == Color.Transparent)
                                {
                                    acf.BackColor = acf.TransparencyKey;
                                }
                                acf.Left = acf.EvaluateNumericExpression(ctx, a.TextAuraXIniExpression);
                                acf.Top = acf.EvaluateNumericExpression(ctx, a.TextAuraYIniExpression);
                                int i = acf.EvaluateNumericExpression(ctx, a.TextAuraWIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                acf.Width = i;
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                i = acf.EvaluateNumericExpression(ctx, a.TextAuraHIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                acf.Height = i;
                                i = acf.EvaluateNumericExpression(ctx, a.TextAuraOIniExpression);
                                if (i < 0)
                                {
                                    i = 0;
                                }
                                if (i > 100)
                                {
                                    i = 100;
                                }
                                acf.PresentableOpacity = i;
                                acf.XExpression = a.TextAuraXTickExpression;
                                acf.YExpression = a.TextAuraYTickExpression;
                                acf.WExpression = a.TextAuraWTickExpression;
                                acf.HExpression = a.TextAuraHTickExpression;
                                acf.OExpression = a.TextAuraOTickExpression;
                                acf.TTLExpression = a.TextAuraTTLTickExpression;
                                acf.AuraActivate();
                                if (newAura == true)
                                {
                                    ctx.plug.textauras[ax] = acf;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/exacttextaura", "Exception '{0}' when activating text aura '{1}'", ex.Message, ax));
                            if (acf != null)
                            {
                                if (newAura == true)
                                {
                                    acf.Dispose();
                                }
                                else
                                {
                                    acf.AuraDeactivate();
                                }
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAura:
                    {
                        string ax = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.TextAuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deacttextaura", "Deactivating text aura '{0}'", ax));
                        lock (ctx.plug.textauras)
                        {
                            if (ctx.plug.textauras.ContainsKey(ax) == true)
                            {
                                Forms.AuraContainerForm acf = ctx.plug.textauras[ax];
                                acf.AuraDeactivate();
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAllAura:
                    {
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deactalltextaura", "Deactivating all text auras"));
                        while (true)
                        {
                            lock (ctx.plug.textauras)
                            {
                                if (ctx.plug.textauras.Count > 0)
                                {
                                    Forms.AuraContainerForm acf = ctx.plug.textauras.ElementAt(0).Value;
                                    acf.AuraDeactivate();
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }
                    break;
                case Action.AuraOpEnum.DeactivateAuraRegex:
                    {
                        Regex rex = new Regex(a.AuraName);
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/deacttextaurarex", "Deactivating text auras matching '{0}'", a.AuraName));
                        List<string> toRem = new List<string>();
                        lock (ctx.plug.textauras)
                        {
                            toRem.AddRange(from sx in ctx.plug.textauras where rex.IsMatch(sx.Key) select sx.Key);
                            foreach (string rem in toRem)
                            {
                                Forms.AuraContainerForm acf = ctx.plug.textauras[rem];
                                acf.AuraDeactivate();
                            }
                        }
                    }
                    break;
            }
        }

        internal class QueuedAction : IComparable
        {

            internal DateTime when { get; set; }
            internal Int64 ordinal { get; set; }
            internal Action act { get; set; }
            internal Context ctx { get; set; }

            public QueuedAction(DateTime when, Int64 ordinal, Action act, Context ctx)
            {
                this.when = when;
                this.ordinal = ordinal;
                this.act = act;
                this.ctx = ctx;
            }

            public int CompareTo(object o)
            {
                QueuedAction b = (QueuedAction)o;
                int ex = when.CompareTo(b.when);
                if (ex != 0)
                {
                    return ex;
                }
                return ordinal.CompareTo(b.ordinal);
            }

        }

        public RealPlugin()
        {
            ui = null;
            sc = null;
            DisableLogging = false;
            path = null;
            ActionQueue = new List<QueuedAction>();
            log = new Queue<InternalLog>();
            EventQueue = new Queue<LogEvent>();
            QueueWakeupEvent = null;
            configBroken = false;
            curOrdinal = 0;
            ExitEvent = null;
            QueueProcessingLock = new object();
            QueueProcessing = true;
            ActionQueueThread = null;
            EventQueueThread = null;
            AuraUpdateThread = null;
            ActionUpdateEvent = null;
            LastDelayWarning = DateTime.Now;
            Triggers = new List<Trigger>();
            ActiveTextTriggers = new List<Trigger>();
            ActiveFFXIVNetworkTriggers = new List<Trigger>();
            simplevariables = new Dictionary<string, Variable>();
            listvariables = new Dictionary<string, VariableList>();
            imageauras = new Dictionary<string, Forms.AuraContainerForm>();
            textauras = new Dictionary<string, Forms.AuraContainerForm>();
            ThreadPool.SetMinThreads(10, 10);
            PluginBridges.BridgeFFXIV.OnLogEvent += BridgeFFXIV_OnLogEvent;
        }

        private void BridgeFFXIV_OnLogEvent(DebugLevelEnum level, string text)
        {
            FilteredAddToLog(level, text);
        }

        internal void AddTrigger(Trigger t)
        {
            lock (Triggers)
            {
                Triggers.Add(t);
                if (t.Enabled == true && t.Parent.ParentsEnabled() == true)
                {
                    switch (t.Source)
                    {
                        case Trigger.TriggerSourceEnum.Log:
                            lock (ActiveTextTriggers)
                            {
                                ActiveTextTriggers.Add(t);
                            }
                            break;
                        case Trigger.TriggerSourceEnum.FFXIVNetwork:
                            lock (ActiveFFXIVNetworkTriggers)
                            {
                                ActiveFFXIVNetworkTriggers.Add(t);
                            }
                            break;
                    }
                }
            }
        }

        internal void SourceChange(Trigger t, Trigger.TriggerSourceEnum oldSource, Trigger.TriggerSourceEnum newSource)
        {
            if (t.Enabled == true && t.Parent.ParentsEnabled() == true)
            {
                switch (oldSource)
                {
                    case Trigger.TriggerSourceEnum.Log:
                        lock (ActiveTextTriggers)
                        {
                            ActiveTextTriggers.Remove(t);
                        }
                        break;
                    case Trigger.TriggerSourceEnum.FFXIVNetwork:
                        lock (ActiveFFXIVNetworkTriggers)
                        {
                            ActiveFFXIVNetworkTriggers.Remove(t);
                        }
                        break;
                }
                switch (newSource)
                {
                    case Trigger.TriggerSourceEnum.Log:
                        lock (ActiveTextTriggers)
                        {
                            ActiveTextTriggers.Add(t);
                        }
                        break;
                    case Trigger.TriggerSourceEnum.FFXIVNetwork:
                        lock (ActiveFFXIVNetworkTriggers)
                        {
                            ActiveFFXIVNetworkTriggers.Add(t);
                        }
                        break;
                }
            }
        }

        internal void RemoveTrigger(Trigger t)
        {
            lock (Triggers)
            {
                switch (t.Source)
                {
                    case Trigger.TriggerSourceEnum.Log:
                        lock (ActiveTextTriggers)
                        {
                            if (ActiveTextTriggers.Contains(t) == true)
                            {
                                ActiveTextTriggers.Remove(t);
                            }
                        }
                        break;
                    case Trigger.TriggerSourceEnum.FFXIVNetwork:
                        lock (ActiveFFXIVNetworkTriggers)
                        {
                            if (ActiveFFXIVNetworkTriggers.Contains(t) == true)
                            {
                                ActiveFFXIVNetworkTriggers.Remove(t);
                            }
                        }
                        break;
                }
                Triggers.Remove(t);
            }
        }

        internal void TriggerEnabled(Trigger t)
        {
            switch (t.Source)
            {
                case Trigger.TriggerSourceEnum.Log:
                    lock (ActiveTextTriggers)
                    {
                        if (ActiveTextTriggers.Contains(t) == false)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigaddbook", "Trigger '{0}' added to bookkeeping", t.LogName));
                            ActiveTextTriggers.Add(t);
                            return;
                        }
                    }
                    break;
                case Trigger.TriggerSourceEnum.FFXIVNetwork:
                    lock (ActiveFFXIVNetworkTriggers)
                    {
                        if (ActiveFFXIVNetworkTriggers.Contains(t) == false)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigaddbook", "Trigger '{0}' added to bookkeeping", t.LogName));
                            ActiveFFXIVNetworkTriggers.Add(t);
                            return;
                        }
                    }
                    break;
            }
        }

        internal void TriggerDisabled(Trigger t)
        {
            switch (t.Source)
            {
                case Trigger.TriggerSourceEnum.Log:
                    lock (ActiveTextTriggers)
                    {
                        if (ActiveTextTriggers.Contains(t) == true)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigrembook", "Trigger '{0}' removed from bookkeeping", t.LogName));
                            ActiveTextTriggers.Remove(t);
                            return;
                        }
                    }
                    break;
                case Trigger.TriggerSourceEnum.FFXIVNetwork:
                    lock (ActiveFFXIVNetworkTriggers)
                    {
                        if (ActiveFFXIVNetworkTriggers.Contains(t) == true)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigrembook", "Trigger '{0}' removed from bookkeeping", t.LogName));
                            ActiveFFXIVNetworkTriggers.Remove(t);
                            return;
                        }
                    }
                    break;
            }
        }

        internal void ClearActionQueue()
        {
            lock (ActionQueue) // verified
            {
                ActionQueue.Clear();
            }
        }

        internal Trigger GetTriggerById(Guid id, Repository repo)
        {
            lock (Triggers)
            {
                var ix = from ax in Triggers
                         where ax.Id == id && ax.Repo == repo
                         select ax;
                return ix.FirstOrDefault();
            }
        }

        internal Folder GetFolderById(Guid id, Repository repo)
        {
            if (repo != null)
            {                
                return RecursiveFolderSearch(repo.Root, id, repo);
            }
            else
            {
                return RecursiveFolderSearch(cfg.Root, id, repo);
            }
        }

        internal Folder RecursiveFolderSearch(Folder f, Guid id, Repository repo)
        {
            if (f.Id == id && f.Repo == repo)
            {
                return f;
            }
            foreach (Folder c in f.Folders)
            {
                Folder ex = RecursiveFolderSearch(c, id, repo);
                if (ex != null)
                {
                    return ex;
                }
            }
            return null;
        }

        internal TreeNode LocateNodeHostingTrigger(TreeNode tn, Trigger t)
        {
            if (tn.Tag == t)
            {
                return tn;
            }
            foreach (TreeNode tc in tn.Nodes)
            {
                TreeNode tp = LocateNodeHostingTrigger(tc, t);
                if (tp != null)
                {
                    return tp;
                }
            }
            return null;
        }

        internal TreeNode LocateNodeHostingFolder(TreeNode tn, Folder f)
        {
            if (tn.Tag == f)
            {
                return tn;
            }
            foreach (TreeNode tc in tn.Nodes)
            {
                TreeNode tp = LocateNodeHostingFolder(tc, f);
                if (tp != null)
                {
                    return tp;
                }
            }
            return null;
        }

        internal TreeNode LocateNodeHostingTriggerId(TreeNode tn, Guid id, Repository repo)
        {
            Trigger t = GetTriggerById(id, repo);
            if (t == null)
            {
                return null;
            }
            return LocateNodeHostingTrigger(tn, t);
        }

        internal TreeNode LocateNodeHostingFolderId(TreeNode tn, Guid id, Repository repo)
        {
            Folder f = GetFolderById(id, repo);
            if (f == null)
            {
                return null;
            }
            return LocateNodeHostingFolder(tn, f);
        }

        internal void CancelAllQueuedActionsFromTrigger(Trigger t)
        {
            int remd = 0;
            lock (ActionQueue)
            {
                var ix = from ax in ActionQueue
                         where ax.ctx.trig == t
                         select ax;
                if (ix.Count() > 0)
                {
                    List<QueuedAction> rems = new List<QueuedAction>();
                    rems.AddRange(ix);
                    foreach (QueuedAction qa in rems)
                    {
                        ActionQueue.Remove(qa);
                        remd++;
                    }
                    ActionQueue.Sort();
                    ActionUpdateEvent.Set();
                }
            }
            t.AddToLog(this, DebugLevelEnum.Info, I18n.Translate("internal/Plugin/queuedactscancelled", "{0} queued action(s) from trigger '{1}' cancelled", remd, t.LogName));
        }

        internal void TtsPlaybackAct(Context ctx, Action a)
        {
            string text = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.UseTTSTextExpression);
            TtsPlaybackHook(text);
        }

        internal void SoundPlaybackAct(Context ctx, Action a)
        {
            double vol = ctx.EvaluateNumericExpression(a.ActionContextLogger, ctx, a.PlaySoundVolumeExpression);
            vol *= (ctx.plug.cfg.SfxVolumeAdjustment / 100.0);
            if (vol < 0.0)
            {
                vol = 0.0;
            }
            if (vol > 100.0)
            {
                vol = 100.0;
            }
            string filename = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.PlaySoundFileExpression);
            SoundPlaybackHook(filename, (int)Math.Floor(vol));
        }

        internal void TtsPlaybackSelf(Context ctx, Action a)
        {
            SpeechSynthesizer mytts;
            if (a._UseTTSExclusive == true)
            {
                mytts = new SpeechSynthesizer();
            }
            else
            {
                mytts = tts;
            }
            double vol = ctx.EvaluateNumericExpression(a.ActionContextLogger, ctx, a.UseTTSVolumeExpression);
            vol *= (ctx.plug.cfg.TtsVolumeAdjustment / 100.0);
            if (vol < 0.0)
            {
                vol = 0.0;
            }
            if (vol > 100.0)
            {
                vol = 100.0;
            }
            double rate = ctx.EvaluateNumericExpression(a.ActionContextLogger, ctx, a.UseTTSRateExpression);
            if (rate < -10.0)
            {
                rate = -10.0;
            }
            if (rate > 10.0)
            {
                rate = 10.0;
            }
            mytts.Volume = (int)Math.Ceiling(vol);
            mytts.Rate = (int)Math.Ceiling(rate);
            mytts.Speak(ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.UseTTSTextExpression));
        }

        internal void SoundPlaybackSelf(Context ctx, Action a)
        {
            WindowsMediaPlayer mywmp;
            if (a._PlaySoundExclusive == true)
            {
                mywmp = new WindowsMediaPlayer();                
                lock (a.players) // verified
                {
                    a.players.Add(mywmp);
                }
                mywmp.MediaError += a.Mywmp_MediaError;
                mywmp.PlayStateChange += a.Mywmp_PlayStateChange;
            }
            else
            {
                mywmp = wmp;
            }
            double vol = ctx.EvaluateNumericExpression(a.ActionContextLogger, ctx, a.PlaySoundVolumeExpression);
            vol *= (ctx.plug.cfg.SfxVolumeAdjustment / 100.0);
            if (vol < 0.0)
            {
                vol = 0.0;
            }
            if (vol > 100.0)
            {
                vol = 100.0;
            }
            mywmp.URL = "";
            mywmp.settings.volume = (int)Math.Ceiling(vol);
            mywmp.URL = ctx.EvaluateStringExpression(a.ActionContextLogger, ctx, a.PlaySoundFileExpression);
        }

        internal void TtsPlaybackSmart(Context ctx, Action a)
        {
            if (cfg.UseACTForTTS == true && a._PlaySpeechMyself == false)
            {
                TtsPlaybackAct(ctx, a);
            }
            else
            {
                TtsPlaybackSelf(ctx, a);
            }
        }

        internal void SoundPlaybackSmart(Context ctx, Action a)
        {
            if (WMPUnavailable == true || (cfg.UseACTForSound == true && a._PlaySoundMyself == false))
            {
                SoundPlaybackAct(ctx, a);
            }
            else
            {
                SoundPlaybackSelf(ctx, a);
            }
        }

        public void GenericExceptionHandler(string msg, Exception ex)
        {
            MessageBox.Show(ui, msg + ": " + Environment.NewLine + Environment.NewLine + ex.Message + " " + ex.StackTrace, I18n.Translate("internal/Plugin/exception", "Exception"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        internal static string FormatDateTime(DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }

        private bool errorWarnState = false;

        internal void ClearLog()
        {
            lock (log)
            {
                log.Clear();
            }
            if (errorWarnState == true)
            {
                ui.HideErrorThing(null, null);                
            }
        }

        internal void UnfilteredAddToLog(DebugLevelEnum level, string msg)
        {
            InternalLog il = new InternalLog() { Timestamp = DateTime.Now, Level = level, Message = msg };
            if (DisableLogging == false && System.Diagnostics.Debugger.IsAttached == true)
            {
                System.Diagnostics.Debug.WriteLine(il.ToString());
            }
            if (level == DebugLevelEnum.Error && errorWarnState == false)
            {
                if (ui != null)
                {
                    ui.ShowErrorThing(null, null);
                }
                errorWarnState = true;
            }
            lock (log) // verified
            {
                log.Enqueue(il);
                if (log.Count > 100000)
                {
                    log.Dequeue();
                }
            }
        }

        internal void FilteredAddToLog(DebugLevelEnum level, string msg)
        {
            if (cfg != null && level > cfg.DebugLevel)
            {
                return;
            }
            UnfilteredAddToLog(level, msg);
        }

        internal void ChangeLanguage(string langname)
        {
            FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/langchange", "Changing language from '{0}' to '{1}'",
                I18n.CurrentLanguage != null ? I18n.CurrentLanguage.LanguageName : "(not set)",
                (langname != null ? langname : "(default)")
            ));
            if (I18n.ChangeLanguage(langname) == true)
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/langchangeok", "Language is now '{0}'",
                    I18n.CurrentLanguage != null ? I18n.CurrentLanguage.LanguageName : "(not set)"
                ));
                if (cfg != null && I18n.CurrentLanguage != null)
                {
                    if (langname == null)
                    {
                        cfg.Language = null;
                    }
                    else
                    {
                        cfg.Language = I18n.CurrentLanguage.LanguageName;
                    }
                }
            }
            else
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/langchangefail", "Couldn't change language from '{0}' to '{1}'",
                    I18n.CurrentLanguage != null ? I18n.CurrentLanguage.LanguageName : "(not set)",
                    (langname != null ? langname : "(default)")
                ));
            }
        }

        private Language LoadLanguage(string filename)
        {
            try
            {
                string x = I18n.Translate("internal/Plugin/langload", "Loading language from '{0}'", filename);
                FilteredAddToLog(DebugLevelEnum.Info, x);
                FileInfo fi = new FileInfo(filename);
                if (fi.Exists == false)
                {
                    FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/Plugin/langfilenotfound", "Language file '{0}' does not exist", filename));
                    return null;
                }
                XmlSerializer xs = new XmlSerializer(typeof(Language));
                using (FileStream fs = File.Open(filename, FileMode.Open, FileAccess.Read))
                {
                    Language l = (Language)xs.Deserialize(fs);
                    l.IsDefault = false;
                    l.BuildLookup();
                    return l;
                }
            }
            catch (Exception ex)
            {
                FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/langloadfail", "Loading language file failed, make sure you are running the latest version"));
                GenericExceptionHandler(I18n.Translate("internal/Plugin/langloadex", "Loading the language file '{0}' failed due to an exception", filename), ex);
            }
            return null;
        }

        private void SaveDefaultLanguage(string filename)
        {
            Language l = I18n.DefaultLanguage;
            l.BuildList();
            XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
            ns.Add("", "");
            XmlSerializer xs = new XmlSerializer(typeof(Language));
            string test = "";
            using (MemoryStream ms = new MemoryStream())
            {
                xs.Serialize(ms, l, ns);
                ms.Position = 0;
                using (StreamReader sr = new StreamReader(ms))
                {
                    test = sr.ReadToEnd();
                    test = SerializeInvalidXmlCharacters(test);
                }
            }
            using (FileStream fs = File.Open(filename, FileMode.Create, FileAccess.Write))
            {
                using (StreamWriter sw = new StreamWriter(fs))
                {
                    sw.Write(test);
                    sw.Flush();
                }
            }
        }

        private void LoadLanguages()
        {
            {
                string[] files = Directory.GetFiles(path, "*.triglations.xml", SearchOption.TopDirectoryOnly);
                foreach (string file in files)
                {
                    Language l = LoadLanguage(file);
                    if (l != null)
                    {
                        I18n.AddLanguage(l);
                    }
                }
            }
            if (path != pluginPath)
            {
                string[] files = Directory.GetFiles(pluginPath, "*.triglations.xml", SearchOption.TopDirectoryOnly);
                foreach (string file in files)
                {
                    Language l = LoadLanguage(file);
                    if (l != null)
                    {
                        I18n.AddLanguage(l);
                    }
                }
            }
        }

        /*private void CombobulateTranslations()
        {
            string[] ex = File.ReadAllLines(@"F:\Work\Programming\Net\Triggernometry\Triggernometry\newtranslations.txt");
            XmlDocument doc = new XmlDocument();
            XmlNode n = doc.CreateElement("bla");
            doc.AppendChild(n);
            string tag = "", trans = "";
            for (int i = 0; i < ex.Length; i++)
            {
                if ((i % 3) == 0)
                {
                    tag = ex[i];
                }
                if ((i % 3) == 1)
                {
                    trans = ex[i];
                    XmlNode sn = doc.CreateElement("TranslationEntry");
                    XmlAttribute a1 = doc.CreateAttribute("Key");
                    a1.Value = tag;
                    XmlAttribute a2 = doc.CreateAttribute("Translation");
                    a2.Value = trans;
                    sn.Attributes.Append(a1);
                    sn.Attributes.Append(a2);
                    n.AppendChild(sn);
                }
            }
            string docx = doc.InnerXml;
        }*/

        private bool _usingScarborough = false;

        private void FixDuplicateFolderReferences(Dictionary<Guid, List<Folder>> references, Configuration c, Folder f)
        {
            if (f == null)
            {
                Dictionary<Guid, List<Folder>> existing = new Dictionary<Guid, List<Folder>>();
                foreach (Folder sf in c.Root.Folders)
                {
                    FixDuplicateFolderReferences(existing, c, sf);
                }
                foreach (KeyValuePair<Guid, List<Folder>> kp in existing)
                {
                    if (kp.Value.Count <= 1)
                    {
                        continue;
                    }
                    Folder ori = kp.Value[0];
                    foreach (Folder refe in kp.Value)
                    {
                        if (refe == ori)
                        {
                            continue;
                        }
                        refe.Id = Guid.NewGuid();
                        FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/UserInterface/folderidreassign", "Reassigning new id ({0}) for folder ({1}) due to already assigned id ({2}) on folder ({3})", refe.Id, refe.Name, ori.Id, ori.Name));
                    }
                }
            }
            else
            {
                if (references.ContainsKey(f.Id) == false)
                {
                    references[f.Id] = new List<Folder>();
                }
                references[f.Id].Add(f);
                foreach (Folder sf in f.Folders)
                {
                    FixDuplicateFolderReferences(references, null, sf);
                }
            }
        }

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {

            Language ld = new Language();
            ld.LanguageName = "English (default)";
            ld.MissingKeyHandling = Language.MissingHandlingEnum.OutputKey;
            I18n.DefaultLanguage = ld;
            I18n.AddLanguage(ld);
            ChangeLanguage(null);
            string exwhere = I18n.Translate("internal/Plugin/initseek", "seeking plugin instance");
            try
            {
                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/initing", "Initializing"));
                //CombobulateTranslations();
                exwhere = I18n.Translate("internal/Plugin/inifilename", "determining filename");
                pluginName = Path.GetFileNameWithoutExtension(pluginName);
                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/filenameis", "Plugin filename is '{0}' at '{1}'", pluginName, pluginPath));
                exwhere = I18n.Translate("internal/Plugin/inilanguages", "loading languages");
                LoadLanguages();
                exwhere = I18n.Translate("internal/Plugin/inicfg", "loading configuration");
                cfg = LoadConfigFromFile(Path.Combine(path, pluginName + ".config.xml"));
                if (cfg != null && cfg._ShowWelcomeHasBeenSet == false && (cfg.Root.Folders.Count > 0 || cfg.Root.Triggers.Count > 0))
                {
                    cfg.ShowWelcome = false;
                }
                BackupConfiguration();
                FixDuplicateFolderReferences(null, cfg, null);
                PluginBridges.BridgeFFXIV.cfg = cfg;
                if (cfg.Language != null)
                {
                    ChangeLanguage(cfg.Language);
                }
                exwhere = I18n.Translate("internal/Plugin/iniactui", "setting up ACT ui");
                mytp = pluginScreenSpace;
                pluginScreenSpace.Text = "Triggernometry";
                exwhere = I18n.Translate("internal/Plugin/inievents", "creating events");
                ExitEvent = new ManualResetEvent(false);
                QueueWakeupEvent = new ManualResetEvent(false);
                ActionUpdateEvent = new AutoResetEvent(false);
                exwhere = I18n.Translate("internal/Plugin/iniui", "creating user interface");
                ui = new CustomControls.UserInterface();
                ui.cfg = cfg;
                I18n.TranslateControl("Plugin", ui);
                ui.Dock = DockStyle.Fill;
                ui.plug = this;
                pluginScreenSpace.Controls.Add(ui);
                WMPUnavailable = false;
                exwhere = I18n.Translate("internal/Plugin/iniwmp", "trying to initialize Windows Media Player");
                try
                {
                    wmp = new WMPLib.WindowsMediaPlayer();
                }
                catch (Exception ex)
                {
                    WMPUnavailable = true;
                    FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/iniwmperror", "Error while {0} ({1}), going to fallback to ACT hooks for sound playback", exwhere, ex.Message));
                }
                exwhere = I18n.Translate("internal/Plugin/initts", "trying to initialize TTS");
                tts = new SpeechSynthesizer();
                ui.wmp = wmp;
                ui.tts = tts;
                exwhere = I18n.Translate("internal/Plugin/iniwelcome", "preparing welcome");
                ui.pnlWelcome.Dock = DockStyle.Fill;
                ui.pnlUi.Dock = DockStyle.Fill;
                if (cfg != null && cfg.ShowWelcome == true)
                {
                    ui.pnlUi.Visible = false;
                    ui.pnlWelcome.Visible = true;
                    ui.btnOptions.Enabled = false;
                }
                else
                {
                    ui.pnlUi.Visible = true;
                    ui.pnlWelcome.Visible = false;
                    ui.btnOptions.Enabled = true;
                }
                if (cfg.UpdateNotifications == Configuration.UpdateNotificationsEnum.Yes)
                {
                    exwhere = I18n.Translate("internal/Plugin/iniupdates", "checking for updates");
                    CheckForUpdates();
                }
                exwhere = I18n.Translate("internal/Plugin/initoasts", "setting up toasts");
                ui.SetupToasts();
                ui.SetupLanguageMenu();
                if (cfg.WarnAdmin == true)
                {
                    CheckIfAdministrator();
                }
                exwhere = I18n.Translate("internal/Plugin/initree", "building internal data");
                ui.BuildFullTreeFromConfiguration();
                int PrimaryX = 0, PrimaryY = 0;
                foreach (Screen s in Screen.AllScreens)
                {
                    FilteredAddToLog(DebugLevelEnum.Info, String.Format("{0}{1}: {2},{3} - {4},{5}", s.DeviceName, s.Primary == true ? " (*)" : "", s.Bounds.Left, s.Bounds.Top, s.Bounds.Left + s.Bounds.Width, s.Bounds.Top + s.Bounds.Height));
                    if (s.WorkingArea.Left < MinX)
                    {
                        MinX = s.WorkingArea.Left;
                    }
                    if (s.WorkingArea.Top < MinY)
                    {
                        MinY = s.WorkingArea.Top;
                    }
                    if (s.WorkingArea.Left + s.WorkingArea.Width > MaxX)
                    {
                        MaxX = s.WorkingArea.Left + s.WorkingArea.Width;
                    }
                    if (s.WorkingArea.Top + s.WorkingArea.Height > MaxY)
                    {
                        MaxY = s.WorkingArea.Top + s.WorkingArea.Height;
                    }
                    if (s.Primary == true)
                    {
                        PrimaryX = s.WorkingArea.Left;
                        PrimaryY = s.WorkingArea.Top;
                    }
                }
                FilteredAddToLog(DebugLevelEnum.Info, String.Format("*: {0},{1} - {2},{3}", MinX, MinY, MaxX, MaxY));
                if (cfg.UseScarborough == true)
                {
                    sc = new Scarborough.Scarborough();
                    sc.plug = this;
                    _usingScarborough = true;
                }
                ActionQueueThread = new Thread(new ThreadStart(ActionThreadProc));
                ActionQueueThread.Name = "ActionQueueThread";
                ActionQueueThread.Start();
                EventQueueThread = new Thread(new ThreadStart(LogLineProcessorThread));
                EventQueueThread.Name = "EventQueueThread";
                EventQueueThread.Start();
                AuraUpdateThread = new Thread(new ThreadStart(AuraUpdateThreadProc));
                AuraUpdateThread.Name = "AuraUpdateThread";
                AuraUpdateThread.Start();
                _obs = new ObsController();                
                pluginStatusText.Text = I18n.Translate("internal/Plugin/iniready", "Ready");
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/inited", "Initialized"));
                Task tx = new Task(() =>
                {
                    RepositoryUpdates();
                });
                tx.Start();
            }
            catch (Exception ex)
            {
                pluginStatusText.Text = I18n.Translate("internal/Plugin/inierror", "Error while {0} ({1})", exwhere, ex.Message);
            }
        }

        private void ShowProgress(int progress, string state)
        {
            ui.ShowProgress(progress, state);
        }

        private void ClearRepository(Repository r)
        {
            ui.ClearRepository(r);
        }

        internal void RepositoryUpdate(Repository r, bool alone)
        {
            ClearRepository(r);
            r.ClearLog();
            string trans;
            bool useBackup = false;
            try
            {
                if (alone == true)
                {
                    trans = I18n.Translate("internal/Plugin/repoupdate", "Updating repository {0} at {1}", r.Name, r.Address);
                    FilteredAddToLog(DebugLevelEnum.Verbose, trans);
                    r.AddToLog(trans);
                    ShowProgress(-1, trans);
                }
                if (r.UpdatePolicy == Repository.UpdatePolicyEnum.Startup)
                {
                    System.Threading.Thread.Sleep(500);
                    DateTime remdate = DateTime.MinValue;
                    long remsize = 0, localsize = 0;
                    using (System.Net.Http.HttpClient httpClient = new System.Net.Http.HttpClient())
                    {
                        System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, new Uri(r.Address));
                        Task<System.Net.Http.HttpResponseMessage> tsk = httpClient.SendAsync(request);
                        System.Net.Http.HttpResponseMessage response = tsk.Result;
                        DateTimeOffset? lastmod = response.Content.Headers.LastModified;
                        if (lastmod.HasValue == true)
                        {
                            remdate = lastmod.Value.DateTime;
                        }
                        else
                        {
                            remdate = r.LastUpdated;
                        }
                        long? conlen = response.Content.Headers.ContentLength;
                        if (conlen.HasValue == true)
                        {
                            if (conlen.Value > 0)
                            {
                                remsize = conlen.Value;
                            }
                            else
                            {
                                remsize = 0;
                            }
                        }
                        else
                        {
                            remsize = 0;
                        }
                    }
                    if (r.KeepLocalBackup == true)
                    {
                        string repofn = GetRepositoryBackupFilename(r);
                        if (File.Exists(repofn) == true)
                        {
                            FileInfo fi = new FileInfo(repofn);
                            localsize = fi.Length;
                        }
                    }
                    if (remdate == r.LastUpdated && remsize == localsize && localsize > 0 && r.KeepLocalBackup == true)
                    {
                        trans = I18n.Translate("internal/Plugin/repousingbackup", "Repository {0} hasn't changed since {1}, and size {2} hasn't changed from {3}, using local backup", r.Name, remdate, remsize, localsize);
                        FilteredAddToLog(DebugLevelEnum.Info, trans);
                        r.AddToLog(trans);
                        if (LoadLocalBackupForRepository(r) == true)
                        {
                            if (alone == true)
                            {
                                trans = I18n.Translate("internal/Plugin/repoupdatecomplete", "Repository update complete");
                                r.AddToLog(trans);
                                ShowProgress(100, trans);
                                System.Threading.Thread.Sleep(2000);
                                ShowProgress(0, "");
                            }
                            return;
                        }
                    }
                    else
                    {
                        r.LastUpdated = remdate;
                    }
                    string data;
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers["User-Agent"] = "Triggernometry Repository Updater";
                        byte[] rawdata = wc.DownloadData(r.Address);
                        data = Encoding.UTF8.GetString(rawdata);
                    }
                    TriggernometryExport exp = TriggernometryExport.Unserialize(data);
                    if (exp != null)
                    {
                        r.ContentSize = data.Length;
                        AddContentToRepository(exp, r);
                        if (r.KeepLocalBackup == true)
                        {
                            SaveLocalBackupForRepository(r, data);
                        }
                    }
                    else
                    {
                        trans = I18n.Translate("internal/Plugin/repoexportnull", "Data for repository {0} could not be unserialized", r.Name);
                        FilteredAddToLog(DebugLevelEnum.Error, trans);
                        r.AddToLog(trans);
                    }
                }
                if (r.UpdatePolicy == Repository.UpdatePolicyEnum.Manual)
                {
                    useBackup = true;
                }
            }
            catch (Exception ex)
            {
                trans = I18n.Translate("internal/Plugin/repoupdateexception", "Couldn't update repository {0} due to exception: {1}", r.Name, ex.Message);
                r.AddToLog(trans);
                FilteredAddToLog(DebugLevelEnum.Error, trans);
                useBackup = true;
            }
            if (useBackup == true && r.KeepLocalBackup == true)
            {
                trans = I18n.Translate("internal/Plugin/repousinglocal", "Loading local backup of repository {0}", r.Name);
                FilteredAddToLog(DebugLevelEnum.Info, trans);
                r.AddToLog(trans);
                LoadLocalBackupForRepository(r);
            }
            if (alone == true)
            {
                trans = I18n.Translate("internal/Plugin/repoupdatecomplete", "Repository update complete");
                r.AddToLog(trans);
                ShowProgress(100, trans);
                System.Threading.Thread.Sleep(2000);
                ShowProgress(0, "");
            }
        }

        internal void RepositoryUpdates()
        {
            List<Repository> lr = new List<Repository>();
            lr.AddRange(from ix in cfg.RepositoryRoot.Repositories where ix.Enabled == true select ix);
            if (lr.Count == 0)
            {
                return;
            }
            string trans = I18n.Translate("internal/Plugin/repoupdates", "Going to update {0} repositories", lr.Count);
            FilteredAddToLog(DebugLevelEnum.Info, trans);
            ShowProgress(-1, trans);
            int done = -1, doing = lr.Count;
            foreach (Repository r in lr)
            {
                if (ExitEvent.WaitOne(0) == true)
                {
                    return;
                }
                done++;
                trans = I18n.Translate("internal/Plugin/repoupdate", "Updating repository {0} at {1}", r.Name, r.Address);
                FilteredAddToLog(DebugLevelEnum.Verbose, trans);
                r.AddToLog(trans);
                ShowProgress((int)Math.Floor(100.0 * (float)done / (float)doing), trans);
                RepositoryUpdate(r, false);
            }
            trans = I18n.Translate("internal/Plugin/repoupdatecomplete", "Repository update complete");
            ShowProgress(100, trans);
            System.Threading.Thread.Sleep(2000);
            ShowProgress(0, "");
        }

        private void ApplyRepositoryRestrictions(Folder f, Repository r)
        {
            var ix = from tx in r.FolderStates where tx.Id == f.Id select tx;
            if (ix.Count() == 0)
            {
                switch (r.NewBehavior)
                {
                    case Repository.NewBehaviorEnum.AlwaysEnable:
                        f.Enabled = true;
                        break;
                    case Repository.NewBehaviorEnum.AlwaysDisable:
                        f.Enabled = false;
                        break;
                }
                r.FolderStates.Add(new Repository.RepositoryItem() { Id = f.Id, Enabled = f.Enabled });
            }
            else
            {
                Repository.RepositoryItem ri = ix.First();
                f.Enabled = ri.Enabled;
            }
            foreach (Folder subf in f.Folders)
            {
                ApplyRepositoryRestrictions(subf, r);
            }
            List<Trigger> torem = new List<Trigger>();
            foreach (Trigger t in f.Triggers)
            {
                if (ApplyRepositoryRestrictions(t, r) == false)
                {
                    torem.Add(t);
                }
            }
            foreach (Trigger t in torem)
            {
                f.Triggers.Remove(t);
            }
            f.Repo = r;
        }

        private bool ApplyRepositoryRestrictions(Trigger t, Repository r)
        {
            foreach (Action a in t.Actions)
            {
                if (a.ActionType == Action.ActionTypeEnum.ExecuteScript && r.AllowScriptExecution == false)
                {
                    return false;
                }
                if (a.ActionType == Action.ActionTypeEnum.LaunchProcess && r.AllowProcessLaunch == false)
                {
                    return false;
                }
                if (a.ActionType == Action.ActionTypeEnum.WindowMessage&& r.AllowWindowMessages == false)
                {
                    return false;
                }
                if (a.ActionType == Action.ActionTypeEnum.ObsControl && r.AllowObsControl == false)
                {
                    return false;
                }
            }
            var ix = from tx in r.TriggerStates where tx.Id == t.Id select tx;
            if (ix.Count() == 0)
            {
                switch (r.NewBehavior)
                {
                    case Repository.NewBehaviorEnum.AlwaysEnable:
                        t.Enabled = true;
                        break;
                    case Repository.NewBehaviorEnum.AlwaysDisable:
                        t.Enabled = false;
                        break;
                }
                r.TriggerStates.Add(new Repository.RepositoryItem() { Id = t.Id, Enabled = t.Enabled });
            }
            else
            {
                Repository.RepositoryItem ri = ix.First();
                t.Enabled = ri.Enabled;
            }
            switch (r.AudioOutput)
            {
                case Repository.AudioOutputEnum.AlwaysOverride:
                    {
                        foreach (Action a in t.Actions)
                        {
                            a._PlaySoundMyself = true;
                            a._PlaySpeechMyself = true;
                        }
                    }
                    break;
                case Repository.AudioOutputEnum.NeverOverride:
                    {
                        foreach (Action a in t.Actions)
                        {
                            a._PlaySoundMyself = false;
                            a._PlaySpeechMyself = false;
                        }
                    }
                    break;
            }
            t.Repo = r;
            return true;
        }

        private void RegisterRepositoryFolder(Repository r, Folder f, bool parentenable)
        {
            if (f.Enabled == false)
            {
                parentenable = false;
            }
            foreach (Folder fs in f.Folders)
            {
                fs.Parent = f;
                RegisterRepositoryFolder(r, fs, parentenable);
            }
            foreach (Trigger t in f.Triggers)
            {
                t.Parent = f;
                RegisterRepositoryTrigger(r, t, parentenable);
            }
        }

        private void RegisterRepositoryTrigger(Repository r, Trigger t, bool parentenable)
        {
            if (t.Enabled == true && parentenable == true)
            {
                AddTrigger(t);
            }
        }

        private void AddContentToRepository(TriggernometryExport exp, Repository r)
        {
            if (exp.ExportedFolder != null)
            {
                ApplyRepositoryRestrictions(exp.ExportedFolder, r);
                r.Root.Folders.Add(exp.ExportedFolder);
                RegisterRepositoryFolder(r, exp.ExportedFolder, r.Enabled);
            }
            if (exp.ExportedTrigger != null)
            {
                if (ApplyRepositoryRestrictions(exp.ExportedTrigger, r) == false)
                {
                    return;
                }
                r.Root.Triggers.Add(exp.ExportedTrigger);
                exp.ExportedTrigger.Parent = r.Root;
                RegisterRepositoryTrigger(r, exp.ExportedTrigger, r.Enabled);
            }
            ui.BuildTreeForRepository(exp, r);
        }

        internal string HashRepositoryAddress(string addy)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(addy);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2"));
                }
                return sb.ToString().ToLower();
            }
        }

        internal string GetRepositoryBackupFilename(Repository r)
        {
            string fn = Path.Combine(path, "TriggernometryRepoBackups");
            return Path.Combine(fn, HashRepositoryAddress(r.Address) + ".xml");
        }

        private bool LoadLocalBackupForRepository(Repository r)
        {
            string trans;
            try
            {
                string fn = GetRepositoryBackupFilename(r);
                trans = I18n.Translate("internal/Plugin/repoloadinglocal", "Loading local backup of repository {0} in {1}", r.Name, fn);
                FilteredAddToLog(DebugLevelEnum.Verbose, trans);
                r.AddToLog(trans);
                string data = File.ReadAllText(fn);
                TriggernometryExport exp = TriggernometryExport.Unserialize(data);
                if (exp != null)
                {
                    r.ContentSize = data.Length;
                    AddContentToRepository(exp, r);
                    trans = I18n.Translate("internal/Plugin/repoloadedlocal", "Loaded local backup of repository {0} from {1}", r.Name, fn);
                    FilteredAddToLog(DebugLevelEnum.Info, trans);
                    r.AddToLog(trans);
                    return true;
                }
                else
                {
                    trans = I18n.Translate("internal/Plugin/repoexportnull", "Data for repository {0} could not be unserialized", r.Name);
                    FilteredAddToLog(DebugLevelEnum.Error, trans);
                    r.AddToLog(trans);
                }
            }
            catch (Exception ex)
            {
                trans = I18n.Translate("internal/Plugin/repoloadlocalexception", "Couldn't load local backup of repository {0} due to exception: {1}", r.Name, ex.Message);
                FilteredAddToLog(DebugLevelEnum.Error, trans);
                r.AddToLog(trans);
            }
            return false;
        }

        private void SaveLocalBackupForRepository(Repository r, string data)
        {
            string trans;
            try
            {
                string fn = Path.Combine(path, "TriggernometryRepoBackups");
                string fn2 = Path.Combine(fn, HashRepositoryAddress(r.Address) + ".xml");
                trans = I18n.Translate("internal/Plugin/reposavinglocal", "Saving local backup of repository {0} in {1}", r.Name, fn2);
                FilteredAddToLog(DebugLevelEnum.Verbose, trans);
                r.AddToLog(trans);
                if (Directory.Exists(fn) == false)
                {
                    Directory.CreateDirectory(fn);
                }
                File.WriteAllText(fn2, data);
                trans = I18n.Translate("internal/Plugin/reposavedlocal", "Saved local backup of repository {0} in {1}", r.Name, fn2);
                FilteredAddToLog(DebugLevelEnum.Info, trans);
                r.AddToLog(trans);
            }
            catch (Exception ex)
            {
                trans = I18n.Translate("internal/Plugin/reposavelocalexception", "Couldn't save local backup of repository {0} due to exception: {1}", r.Name, ex.Message);
                FilteredAddToLog(DebugLevelEnum.Error, trans);
                r.AddToLog(trans);
            }
        }

        internal void CheckForUpdates()
        {
            string curver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string[] curvers = curver.Split(".".ToArray());
            string newest = curver;
            string[] newests = curvers;
            string newestasset = "";
            try
            {
                string json = "";
                using (WebClient wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "Triggernometry Auto-update";                    
                    byte[] rawdata = wc.DownloadData(@"https://api.github.com/repos/paissaheavyindustries/Triggernometry/releases");
                    json = Encoding.UTF8.GetString(rawdata);
                }
                dynamic releases = new JavaScriptSerializer().DeserializeObject(json);
                foreach (dynamic release in releases)
                {
                    string fullrver = (string)release["tag_name"];
                    string[] rvers = fullrver.Split(".".ToArray());
                    if (rvers[0][0] == 'v')
                    {
                        rvers[0] = rvers[0].Substring(1);
                    }
                    fullrver = String.Join(".", rvers);
                    for (int i = 0; i < 4; i++)
                    {
                        int a = Int32.Parse(newests[i]);
                        int b = Int32.Parse(rvers[i]);
                        if (a > b)
                        {
                            //FilteredAddToLog(DebugLevelEnum.Info, "Newest version " + newest + " > release version " + fullrver);
                            break;
                        }
                        if (a < b)
                        {
                            //FilteredAddToLog(DebugLevelEnum.Info, "Newest version " + newest + " < release version " + fullrver);
                            newest = fullrver;
                            newests = rvers;
                            newestasset = release["assets"][0]["browser_download_url"];
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/vercheckfail", "Version update check failed: {0}", ex.Message));
            }
            if (newest != curver)
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/verchecknew", "Version check: A new version {0} is available for download to replace current version {1}", newest, curver));
                updateDownloadUrl = newestasset;
                CustomControls.Toast t = new CustomControls.Toast();
                t.ToastText = I18n.Translate("internal/Plugin/downloadnewver", "A new version ({0}) is available for download. Would you like to open the download page?", newest);
                t.OnYes += AutoUpdatePromptResult;
                t.OnNo += AutoUpdatePromptResult;
                t.ToastType = CustomControls.Toast.ToastTypeEnum.YesNo;
                ui.QueueToast(t);
            }
            else
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/verchecksame", "Version check: Newest version {0} is the same or older than current version {1}", newest, curver));
            }
        }

        private void AutoUpdatePromptResult(CustomControls.Toast t, bool result)
        {
            if (result == true)
            {
                System.Diagnostics.Process.Start(updateDownloadUrl);
            }
        }

        public void BackupConfiguration()
        {
            string curver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string cfgver = cfg.PluginVersion;
            if (cfgver == null)
            {
                cfgver = "pre1.0.4.4";
            }
            if (cfgver != curver)
            {
                string oldfn = Path.Combine(path, pluginName + ".config.xml");
                string bacfn = Path.Combine(path, pluginName + "." + cfgver + ".config.xml");
                if (File.Exists(oldfn) == true)
                {
                    if (File.Exists(bacfn) == false)
                    {
                        FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/cfgbackupupdate", "Plugin updated from {0} to {1}, backing up configuration as {2}", cfgver, curver, bacfn));
                        File.Copy(oldfn, bacfn, false);
                    }
                    else
                    {
                        FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/Plugin/cfgbackupdatewarn", "Plugin updated from {0} to {1}, but a backup configuration file already exists, not overwriting", cfgver, curver));
                    }
                }
                cfg.PluginVersion = curver;
            }
        }

        public void SaveCurrentConfig()
        {
            SaveConfigToFile(cfg, Path.Combine(path, pluginName + ".config.xml"));
        }

        private void CheckIfAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);                
                bool ret = principal.IsInRole(WindowsBuiltInRole.Administrator);
                if (ret == false)
                {
                    CustomControls.Toast t = new CustomControls.Toast();
                    t.ToastText = I18n.Translate("internal/Plugin/notadministrator", "You are not running ACT as an administrator - this might prevent some triggers from working.");
                    t.ToastType = CustomControls.Toast.ToastTypeEnum.OK;
                    ui.QueueToast(t);
                }
            }
        }

        public void DeInitPlugin()
        {
            ui.CloseForms();
            PluginBridges.BridgeFFXIV.UnsubscribeFromNetworkEvents(this);
            if (_obs != null)
            {
                _obs.Dispose();
                _obs = null;
            }
            if (ExitEvent != null)
            {
                ExitEvent.Set();
            }
            if (ActionQueueThread != null)
            {
                if (ActionQueueThread.Join(5000) == false)
                {
                    ActionQueueThread.Abort();
                }
                ActionQueueThread = null;
            }
            if (EventQueueThread != null)
            {
                if (EventQueueThread.Join(5000) == false)
                {
                    EventQueueThread.Abort();
                }
                EventQueueThread = null;
            }
            if (AuraUpdateThread != null)
            {
                if (AuraUpdateThread.Join(5000) == false)
                {
                    AuraUpdateThread.Abort();
                }
                AuraUpdateThread = null;
            }
            if (sc != null)
            {
                sc.Shutdown();
                sc.Dispose();
                sc = null;
            }
            if (QueueWakeupEvent != null)
            {
                QueueWakeupEvent.Dispose();
                QueueWakeupEvent = null;
            }
            if (configBroken == false)
            {
                SaveCurrentConfig();
            }
            //SaveDefaultLanguage(Path.Combine(path, "default.triglations.xml"));
            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }
            if (tts != null)
            {
                tts.Dispose();
                tts = null;
            }
            if (ui != null)
            {
                ui.Dispose();
                ui = null;
            }
            if (ActionUpdateEvent != null)
            {
                ActionUpdateEvent.Dispose();
                ActionUpdateEvent = null;
            }
            if (ExitEvent != null)
            {
                ExitEvent.Dispose();
                ExitEvent = null;
            }
        }

        internal CancellationToken GetCancellationToken()
        {
            lock (ctslock)
            {
                if (cts == null)
                {
                    cts = new CancellationTokenSource();
                }
                return cts.Token;
            }
        }

        internal void RefreshCancellationToken()
        {
            lock (ctslock)
            {
                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                cts = new CancellationTokenSource();
            }
        }

        internal void TestTrigger(Trigger t, LogEvent le, Action.TriggerForceTypeEnum force)
        {
            lock (t)
            {
                Match m = null;
                if ((force & Action.TriggerForceTypeEnum.SkipRegexp) == 0)
                {
                    m = t.CheckMatch(le.Text);
                    if (m == null)
                    {
                        return;
                    }
                    else
                    {
                        t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigmatches", "Trigger '{0}' matches log line '{1}'", t.LogName, le.Text));
                    }
                }
                if ((force & Action.TriggerForceTypeEnum.SkipActive) == 0)
                {
                    if (t.Enabled == false)
                    {
                        t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trignotactive", "Trigger '{0}' is not active for firing", t.LogName));
                        return;
                    }
                }
                if ((force & Action.TriggerForceTypeEnum.SkipParent) == 0)
                {
                    Folder.FilterFailReason reason = t.Parent.PassesFilter(le.Zone, le.Text);
                    if (reason != Folder.FilterFailReason.Passed)
                    {
                        if (reason != Folder.FilterFailReason.NotEnabled)
                        {
                            t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigparentfail", "Trigger '{0}' doesn't pass parent folder '{1}' filter(s): {2}", t.LogName, t.Parent.Name, reason.ToString()));
                        }
                        return;
                    }
                }
                if ((force & Action.TriggerForceTypeEnum.SkipRefire) == 0)
                {
                    if (t.PeriodRefire == Trigger.RefireEnum.Deny && DateTime.Now < t.RefireDelayedUntil)
                    {
                        t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/trigrefirefail", "Trigger '{0}' refire delayed until {1}", t.LogName, FormatDateTime(t.RefireDelayedUntil)));
                        return;
                    }
                }
                t.AddToLog(this, DebugLevelEnum.Info, I18n.Translate("internal/Plugin/trigfiring", "Firing trigger '{0}'", t.LogName));
                Context ctx = new Context();
                ctx.plug = this;
                ctx.trig = t;
                ctx.soundhook = SoundPlaybackSmart;
                ctx.ttshook = TtsPlaybackSmart;
                if ((force & Action.TriggerForceTypeEnum.SkipRegexp) == 0)
                {
                    foreach (int idx in t.rex.GetGroupNumbers())
                    {
                        ctx.numgroups.Add(m.Groups[idx].Value);
                        t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/debugnumgroup", "Trigger '{0}' numbered group {1}: {2}", t.LogName, idx, m.Groups[idx].Value));
                    }
                    foreach (string sdx in t.rex.GetGroupNames())
                    {
                        ctx.namedgroups[sdx] = m.Groups[sdx].Value;
                        t.AddToLog(this, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/debugnamedgroup", "Trigger '{0}' named group '{1}': {2}", t.LogName, sdx, m.Groups[sdx].Value));
                    }
                }
                ctx.namedgroups["_zone"] = le.Zone;
                ctx.namedgroups["_event"] = le.Text;
                ctx.triggered = DateTime.UtcNow;
                ctx.namedgroups["_timestamp"] = "" + (long)(ctx.triggered - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;
                ctx.namedgroups["_timestampms"] = "" + (long)(ctx.triggered - new DateTime(1970, 1, 1, 0, 0, 0)).TotalMilliseconds;
                ctx.force = force;
                t.Fire(this, ctx);
            }
        }

        internal void LogLineQueuer(string text, string zone, LogEvent.SourceEnum src)
        {
            LogEvent le = new LogEvent();
            le.Text = text;
            le.Zone = zone;
            le.Source = src;
            le.Timestamp = DateTime.Now;
            lock (EventQueue)
            {
                EventQueue.Enqueue(le);
                QueueWakeupEvent.Set();
            }            
        }

        internal void LogLineQueuerMass(IEnumerable<string> text, string zone)
        {
            int max = text.Count();
            int i = 0;
            LogEvent[] lex = new LogEvent[text.Count()];
            foreach (string x in text)
            {
                lex[i] = new LogEvent();
                lex[i].Text = x;
                lex[i].Zone = zone;
                lex[i].Timestamp = DateTime.Now;
                i++;
            }
            if (lex.Count() > 0)
            {
                lock (EventQueue)
                {
                    foreach (LogEvent le in lex)
                    {
                        EventQueue.Enqueue(le);
                    }
                    QueueWakeupEvent.Set();
                }
            }
        }

        private void LogLineProcessorThread()
        {
            WaitHandle[] wh = new WaitHandle[2];
            wh[0] = ExitEvent;
            wh[1] = QueueWakeupEvent;
            if (mainform.IsHandleCreated == false)
            {                
                do
                {
                    Thread.Sleep(100);
                } while (mainform.IsHandleCreated == false);
            }
            while (true)
            {
                switch (WaitHandle.WaitAny(wh, Timeout.Infinite))
                {
                    case 0:
                        {
                            return;
                        }
                    case 1:
                        {
                            LogEvent lx = null;
                            lock (EventQueue)
                            {
                                lx = EventQueue.Dequeue();
                                if (EventQueue.Count == 0)
                                {
                                    QueueWakeupEvent.Reset();
                                }
                            }
                            LogLineProcessor(lx);
                        }
                        break;
                }
            }
        }

        private void VerifyProcessWindow()
        {
            if (cfg.WindowToMonitor != "")
            {
                HideAllAuras = (WindowsUtils.IsInFocus(cfg.WindowToMonitor) == false);
            }
            else
            {
                HideAllAuras = false;
            }
        }

        private void ProcessAuraControl(bool hideAuras)
        {
            if (hideAuras == true)
            {
                if (sc != null)
                {
                    sc.HideAllItems();
                }
                else
                {
                    foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in textauras)
                    {
                        kp.Value.Hide();
                    }
                    foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in imageauras)
                    {
                        kp.Value.Hide();
                    }
                }
            }
            else
            {
                if (sc != null)
                {
                    sc.ShowAllItems();
                }
                else
                {
                    foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in textauras)
                    {
                        kp.Value.Show();
                    }
                    foreach (KeyValuePair<string, Forms.AuraContainerForm> kp in imageauras)
                    {
                        kp.Value.Show();
                    }
                }
            }
        }

        private void AuraUpdateThreadProc()
        {
            WaitHandle[] wh = new WaitHandle[1];
            wh[0] = ExitEvent;
            int numTicks = 0, procticks = 0;
            DateTime prevTick, tickTime;
            if (mainform.IsHandleCreated == false)
            {
                do
                {
                    Thread.Sleep(100);
                } while (mainform.IsHandleCreated == false);
            }
            prevTick = DateTime.Now;
            double msSince, lag = 0.0;
            while (true)
            {
                if (ExitEvent.WaitOne(20) == true)
                {
                    return;
                }
                if (procticks >= 10)
                {
                    VerifyProcessWindow();
                    procticks = 0;
                }
                tickTime = DateTime.Now;
                msSince = (tickTime - prevTick).TotalMilliseconds + lag;
                numTicks = (int)Math.Floor(msSince / 20.0);
                //System.Diagnostics.Debug.WriteLine(String.Format("{0}.{1} -> {2}.{3} = {4}+{5} = {6}x", prevTick, prevTick.Millisecond, tickTime, tickTime.Millisecond, msSince - lag, lag, numTicks));
                lag = msSince - (numTicks * 20);
                prevTick = tickTime;
                UpdateAuras(numTicks);
                procticks++;
            }
        }

        internal void LogLineProcessor(LogEvent le)
        {
            if (firstevent == true)
            {
                PluginBridges.BridgeFFXIV.SubscribeToNetworkEvents(this);
                firstevent = false;
            }
            switch (le.Source)
            {
                case LogEvent.SourceEnum.Log:
                    lock (ActiveTextTriggers) // verified
                    {
                        foreach (Trigger t in ActiveTextTriggers)
                        {
                            TestTrigger(t, le, Action.TriggerForceTypeEnum.NoSkip);
                        }
                    }
                    break;
                case LogEvent.SourceEnum.NetworkFFXIV:
                    lock (ActiveFFXIVNetworkTriggers) // verified
                    {
                        foreach (Trigger t in ActiveFFXIVNetworkTriggers)
                        {
                            TestTrigger(t, le, Action.TriggerForceTypeEnum.NoSkip);
                        }
                    }
                    break;
            }
            double del = (DateTime.Now - le.Timestamp).TotalMilliseconds;
            if (del > 100.0)
            {
                if ((DateTime.Now - LastDelayWarning).TotalSeconds > 10.0)
                {
                    FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/Plugin/warnprocdelay", "Line ({0}) took {1} ms to process, may be falling behind", le.Text, del));
                    LastDelayWarning = DateTime.Now;
                }
            }
        }

        public void OnLogLineRead(bool isImport, string logLine, string detectedZone)
        {
            if (isImport == true)
            {
                return;
            }
            try
            {
                if (cfg.EventSeparator.Length > 0)
                {
                    string[] lines = logLine.Split(new string[] { cfg.EventSeparator }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (logLine.Substring(logLine.Length - 5) != "] FB:")
                        {
                            if (cfg.LogNormalEvents == true)
                            {
                                FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/splitlogline", "Split log line: ({0})", line));
                            }
                            LogLineQueuer(line, detectedZone, LogEvent.SourceEnum.Log);
                        }
                    }
                }
                else
                {
                    if (logLine.Substring(logLine.Length - 5) != "] FB:")
                    {
                        if (cfg.LogNormalEvents == true)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/logline", "Log line: ({0})", logLine));
                        }
                        LogLineQueuer(logLine, detectedZone, LogEvent.SourceEnum.Log);
                    }
                }
            }
            catch (Exception ex)
            {
                FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/procex", "Exception ({0}) when processing log line ({1}) in zone ({2})", ex.Message, logLine, detectedZone));
            }
        }

        public void NetworkLogLineReceiver(uint sequence, int messagetype, string message)
        {
            try
            {
                string preamble = String.Format("{0}|{1}|", messagetype, sequence);
                if (cfg.EventSeparator.Length > 0)
                {
                    string[] lines = message.Split(new string[] { cfg.EventSeparator }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string linex = preamble + line;
                        if (cfg.FfxivLogNetwork == true)
                        {
                            FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/ffxivnetworksplitlogline", "Split network log line: ({0})", linex));
                        }
                        LogLineQueuer(linex, "", LogEvent.SourceEnum.NetworkFFXIV);
                    }
                }
                else
                {
                    string linex = preamble + message;
                    if (cfg.FfxivLogNetwork == true)
                    {
                        FilteredAddToLog(DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/ffxivnetworklogline", "Network log line: ({0})", linex));
                    }
                    LogLineQueuer(linex, "", LogEvent.SourceEnum.NetworkFFXIV);
                }
            }
            catch (Exception ex)
            {
                FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/ffxivnetworkprocex", "Exception ({0}) when processing network log line ({1})", ex.Message, message));
            }
        }

        private Configuration LoadConfigFromFile(string filename)
        {
            try
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/cfgload", "Loading configuration from '{0}'", filename));
                FileInfo fi = new FileInfo(filename);                
                if (fi.Exists == false)
                {
                    FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/Plugin/cfgnew", "Configuration file '{0}' does not exist, creating a new configuration", filename));
                    return new Configuration();
                }
                XmlSerializer xs = new XmlSerializer(typeof(Configuration));                
                using (FileStream fs = File.Open(filename, FileMode.Open, FileAccess.Read))
                {
                    Configuration cx = (Configuration)xs.Deserialize(fs);
                    cx.isnew = false;
                    cx.lastWrite = fi.LastWriteTimeUtc;
                    return cx;
                }
            }
            catch (Exception ex)
            {
                FilteredAddToLog(DebugLevelEnum.Error, I18n.Translate("internal/Plugin/cfgloadfail", "Loading configuration file failed, make sure you are running the latest version - overwriting configuration suppressed"));
                configBroken = true;
                GenericExceptionHandler(I18n.Translate("internal/Plugin/cfgloadex", "Loading the configuration file '{0}' failed due to an exception", filename), ex);
            }
            return null;
        }

        internal static string SerializeInvalidXmlCharacters(string ex)
        {
            ex = ex.Replace("&#x0;", "␀");
            ex = ex.Replace("&#x1;", "␁");
            ex = ex.Replace("&#x2;", "␂");
            ex = ex.Replace("&#x3;", "␃");
            ex = ex.Replace("&#x4;", "␄");
            ex = ex.Replace("&#x5;", "␅");
            ex = ex.Replace("&#x6;", "␆");
            ex = ex.Replace("&#x7;", "␇");
            ex = ex.Replace("&#x8;", "␈");
            ex = ex.Replace("&#x9;", "␉");
            ex = ex.Replace("&#xa;", "␊");
            ex = ex.Replace("&#xb;", "␋");
            ex = ex.Replace("&#xc;", "␌");
            ex = ex.Replace("&#xd;", "␍");
            ex = ex.Replace("&#xe;", "␎");
            ex = ex.Replace("&#xf;", "␏");
            ex = ex.Replace("&#x10;", "␐");
            ex = ex.Replace("&#x11;", "␑");
            ex = ex.Replace("&#x12;", "␒");
            ex = ex.Replace("&#x13;", "␓");
            ex = ex.Replace("&#x14;", "␔");
            ex = ex.Replace("&#x15;", "␕");
            ex = ex.Replace("&#x16;", "␖");
            ex = ex.Replace("&#x17;", "␗");
            ex = ex.Replace("&#x18;", "␘");
            ex = ex.Replace("&#x19;", "␙");
            ex = ex.Replace("&#x1a;", "␚");
            ex = ex.Replace("&#x1b;", "␛");
            ex = ex.Replace("&#x1c;", "␜");
            ex = ex.Replace("&#x1d;", "␝");
            ex = ex.Replace("&#x1e;", "␞");
            ex = ex.Replace("&#x1f;", "␟");
            ex = ex.Replace("&#x7f;", "␡");
            return ex;
        }

        internal static string UnserializeInvalidXmlCharacters(string ex)
        {
            char exx = '\x01';
            string hex = ex;
            ex = ex.Replace("␀", "\x00");
            ex = ex.Replace("␁", "" + exx);
            ex = ex.Replace("␂", "\x02");
            ex = ex.Replace("␃", "\x03");
            ex = ex.Replace("␄", "\x04");
            ex = ex.Replace("␅", "\x05");
            ex = ex.Replace("␆", "\x06");
            ex = ex.Replace("␇", "\x07");
            ex = ex.Replace("␈", "\x08");
            ex = ex.Replace("␉", "\x09");
            ex = ex.Replace("␊", "\x0a");
            ex = ex.Replace("␋", "\x0b");
            ex = ex.Replace("␌", "\x0c");
            ex = ex.Replace("␍", "\x0d");
            ex = ex.Replace("␎", "\x0e");
            ex = ex.Replace("␏", "\x0f");
            ex = ex.Replace("␐", "\x10");
            ex = ex.Replace("␑", "\x11");
            ex = ex.Replace("␒", "\x12");
            ex = ex.Replace("␓", "\x13");
            ex = ex.Replace("␔", "\x14");
            ex = ex.Replace("␕", "\x15");
            ex = ex.Replace("␖", "\x16");
            ex = ex.Replace("␗", "\x17");
            ex = ex.Replace("␘", "\x18");
            ex = ex.Replace("␙", "\x19");
            ex = ex.Replace("␚", "\x1a");
            ex = ex.Replace("␛", "\x1b");
            ex = ex.Replace("␜", "\x1c");
            ex = ex.Replace("␝", "\x1d");
            ex = ex.Replace("␞", "\x1e");
            ex = ex.Replace("␟", "\x1f");
            ex = ex.Replace("␡", "\x7f");
            return ex;
        }

        private void SaveConfigToFile(Configuration cfg, string filename)
        {
            try
            {
                FilteredAddToLog(DebugLevelEnum.Info, I18n.Translate("internal/Plugin/cfgsave", "Saving configuration to '{0}'", filename));
                XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
                string test = "";
                ns.Add("", "");                
                XmlSerializer xs = new XmlSerializer(typeof(Configuration));
                using (MemoryStream ms = new MemoryStream())
                {
                    xs.Serialize(ms, cfg, ns);
                    ms.Position = 0;
                    using (StreamReader sr = new StreamReader(ms))
                    {
                        test = sr.ReadToEnd();
                        test = SerializeInvalidXmlCharacters(test);
                    }
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    using (StreamWriter sw = new StreamWriter(ms))
                    {
                        sw.Write(test);
                        sw.Flush();
                        ms.Position = 0;
                        Configuration cx = (Configuration)xs.Deserialize(ms);
                    }
                }
                using (FileStream fs = File.Open(filename + ".temp", FileMode.Create, FileAccess.Write))
                {
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.Write(test);
                        sw.Flush();
                    }
                }
                if (File.Exists(filename + ".previous") == true)
                {
                    File.Delete(filename + ".previous");
                }
                if (File.Exists(filename) == true)
                {
                    File.Move(filename, filename + ".previous");
                }
                File.Move(filename + ".temp", filename);
            }
            catch (Exception ex)
            {
                GenericExceptionHandler(I18n.Translate("internal/Plugin/cfgsaveex", "Saving the configuration file '{0}' failed due to an exception", filename), ex);
            }
        }

        internal void QueueAction(Context ctx, Trigger t, Action a, DateTime when)
        {
            lock (ActionQueue) // verified
            {                                
                if (a._RefireRequeue == false || a._RefireInterrupt == true)
                {
                    var ix = from ax in ActionQueue
                             where ax.act.Id == a.Id
                             select ax;
                    if (ix.Count() > 0)
                    {
                        if (a._RefireInterrupt == true)
                        {
                            List<QueuedAction> rems = new List<QueuedAction>();
                            rems.AddRange(ix);
                            int exx = 0;
                            foreach (QueuedAction qa in rems)
                            {
                                ActionQueue.Remove(qa);
                                exx++;
                            }
                            if (exx > 0)
                            {
                                a.AddToLog(ctx, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/actionqueuerem", "Removed {0} instance(s) of trigger '{1}' action '{2}' from queue", exx, t.LogName, a.GetDescription(ctx)));
                            }
                        }
                        if (a._RefireRequeue == false)
                        {
                            a.AddToLog(ctx, DebugLevelEnum.Verbose, I18n.Translate("internal/Plugin/actionqueuefail", "Trigger '{0}' action '{1}' not queued, refire requeue disabled", t.LogName, a.GetDescription(ctx)));
                            return;
                        }
                    }
                }
                Int64 newOrdinal;
                lock (this)
                {
                    newOrdinal = curOrdinal;
                    curOrdinal++;
                }
                a.AddToLog(ctx, DebugLevelEnum.Info, I18n.Translate("internal/Plugin/actionqueued", "Queuing trigger '{0}' action '{1}' to {2} slot {3}", t.LogName, a.GetDescription(ctx), FormatDateTime(when), newOrdinal));
                ActionQueue.Add(new QueuedAction(when, newOrdinal, a, ctx));
                ActionQueue.Sort();
                ActionUpdateEvent.Set();
            }
        }

        internal void ActionThreadProc()
        {
            int timeout = Timeout.Infinite;
            WaitHandle[] wh = new WaitHandle[2];
            wh[0] = ExitEvent;
            wh[1] = ActionUpdateEvent;
            if (mainform.IsHandleCreated == false)
            {
                FilteredAddToLog(DebugLevelEnum.Warning, I18n.Translate("internal/Plugin/invalidwindowhandle", "No valid window handle yet, waiting for it to be created"));
                do
                {
                    Thread.Sleep(100);
                } while (mainform.IsHandleCreated == false);
            }
            if (cfg.StartupTriggerId != Guid.Empty)
            {
                if (cfg.StartupTriggerType == Configuration.StartupTriggerTypeEnum.Trigger)
                {
                    Trigger t = GetTriggerById(cfg.StartupTriggerId, null);
                    if (t != null)
                    {
                        LogEvent le = new LogEvent();
                        le.Text = "";
                        le.Zone = "";
                        le.Timestamp = DateTime.Now;
                        TestTrigger(t, le, Action.TriggerForceTypeEnum.SkipAll);
                    }
                }
                if (cfg.StartupTriggerType == Configuration.StartupTriggerTypeEnum.Folder)
                {
                    Folder f = GetFolderById(cfg.StartupTriggerId, null);
                    if (f != null)
                    {
                        LogEvent le = new LogEvent();
                        le.Text = "";
                        le.Zone = "";
                        le.Timestamp = DateTime.Now;
                        foreach (Trigger tx in f.Triggers)
                        {
                            TestTrigger(tx, le, Action.TriggerForceTypeEnum.SkipAll);
                        }
                    }
                }
            }
            while (true)
            { 
                switch (WaitHandle.WaitAny(wh, timeout))
                {
                    case WaitHandle.WaitTimeout:
                        {
                            QueuedAction tp = null;
                            lock (ActionQueue) // verified
                            {
                                if (ActionQueue.Count > 0)
                                { 
                                    tp = ActionQueue[0];
                                    ActionQueue.RemoveAt(0);
                                }
                            }
                            if (tp != null)
                            {
                                tp.act.Execute(tp.ctx);
                                goto case 1;
                            }
                            else
                            {
                                timeout = Timeout.Infinite;
                                continue;
                            }                            
                        }
                    case 0:
                        {
                            return;
                        }
                    case 1:
                        {
                            lock (ActionQueue) // verified
                            {
                                lock (QueueProcessingLock) // verified
                                {
                                    if (QueueProcessing == false)
                                    {
                                        timeout = Timeout.Infinite;
                                        break;
                                    }
                                }
                                if (ActionQueue.Count > 0)
                                {
                                    timeout = (int)Math.Ceiling((ActionQueue[0].when - DateTime.Now).TotalMilliseconds);
                                    if (timeout < 0)
                                    {
                                        timeout = 0;
                                    }
                                }
                                else
                                {
                                    timeout = Timeout.Infinite;
                                }
                            }
                        }
                        break;
                }
            }
        }

    }

}

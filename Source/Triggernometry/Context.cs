﻿using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Web.Script.Serialization;

namespace Triggernometry
{

    internal class Context
    {

		internal bool testmode;
        internal RealPlugin plug;
        internal Trigger trig;
        internal Action.TriggerForceTypeEnum force;

        internal RealPlugin.ActionExecutionHook soundhook;
        internal RealPlugin.ActionExecutionHook ttshook;

        internal static Regex rex = new Regex(@"\$\{(?<id>[^\}\{\$]*)\}");
        internal static Regex rexnum = new Regex(@"\$(?<id>[0-9]+)");
        internal static Regex rexnump = new Regex(@"\[(?<index>.+?)\]\.(?<prop>[a-zA-Z]+)");
        internal static Regex rexlidx = new Regex(@"(?<name>[^\[]+)\[(?<index>.+?)\]");
        internal static Regex rexlprp = new Regex(@"(?<name>[^\.]+)\.(?<prop>[a-zA-Z]+)(\((?<arg>[^\)]+)\)){0,1}");
        internal static Regex rexfunc = new Regex(@"(?<name>[^\(]{1,})(\((?<arg>[^\)]+)\)){0,1}");
        internal Dictionary<string, string> namedgroups;
		internal List<string> numgroups;
        internal DateTime triggered;
        internal string contextResponse = "";
        internal dynamic contextJsonResponse;
        internal bool contextJsonParsed = false;

        internal const double EORZEA_MULTIPLIER = 3600D / 175D;

        private static MathParser mp = new MathParser();
		
		public Context()
		{
			namedgroups = new Dictionary<string, string>();
			numgroups = new List<string>();
        }

        public delegate void LoggerCallback(object o, string message);

        public double EvaluateNumericExpression(LoggerDelegate logger, object o, string expr)
        {
            string exp = ExpandVariables(logger, o, true, expr == null ? "" : expr);
            lock (mp)
            {
                return mp.Parse(exp);
            }
        }

        public string EvaluateStringExpression(LoggerDelegate logger, object o, string expr)
        {            
            string exp = ExpandVariables(logger, o, false, expr == null ? "" : expr);
            return exp;
        }

        public delegate void LoggerDelegate(object o, string msg);

        private TimeSpan GetEorzeanTime()
        {
            long epochTicks = DateTime.UtcNow.Ticks - (new DateTime(1970, 1, 1).Ticks);
            long eorzeaTicks = (long)Math.Round(epochTicks * EORZEA_MULTIPLIER);
            return new DateTime(eorzeaTicks).TimeOfDay;
        }

        private VariableList GetListVariable(RealPlugin p, string varname, bool createNew)
        {
            if (p.listvariables.ContainsKey(varname) == true)
            {
                return p.listvariables[varname];
            }
            VariableList vl = new VariableList();
            if (createNew == true)
            {
                p.listvariables[varname] = vl;
            }
            return vl;
        }

        private static string[] SplitArguments(string args)
        {
            var parmChars = args.ToCharArray();
            var inSingleQuote = false;
            var inDoubleQuote = false;
            for (var index = 0; index < parmChars.Length; index++)
            {
                if (parmChars[index] == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    parmChars[index] = '\n';
                }
                if (parmChars[index] == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    parmChars[index] = '\n';
                }
                if (!inSingleQuote && !inDoubleQuote && parmChars[index] == ',')
                    parmChars[index] = '\n';
            }
            return (new string(parmChars)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public string ExpandVariables(LoggerDelegate logger, object o, bool numeric, string expr)
        {            
            Match m, mx;
            string newexpr = expr;
            int i = 1;
            while (true)
            {
                m = rex.Match(newexpr);
                if (m.Success == false)
                {
                    m = rexnum.Match(newexpr);
                    if (m.Success == false)
                    {
                        break;
                    }
                }
                string x = m.Groups["id"].Value;
                string val = "";
                bool found = false;
                if (testmode == true)
                {
                    if (x == "_since")
                    {
                        val = ((int)Math.Floor((DateTime.UtcNow - triggered).TotalSeconds)).ToString();                        
                    }
                    else if (x == "_sincems")
                    {
                        val = ((int)Math.Floor((DateTime.UtcNow - triggered).TotalMilliseconds)).ToString();                        
                    }
                    else
                    {
                        val = (numeric == true ? "1" : "test");
                    }
                    found = true;
                }
                else
                {
                    int gn;
                    if (Int32.TryParse(x, out gn) == true)
                    {
                        if (gn >= 0 && gn < numgroups.Count)
                        {
                            val = numgroups[gn];
                            found = true;
                        }
                    }
                    if (found == false)
                    {
                        if (namedgroups.ContainsKey(x) == true)
                        {
                            val = namedgroups[x];
                            found = true;
                        }
                    }
                    if (found == false)
                    {
                        if (x == "_since")
                        {
                            val = ((int)Math.Floor((DateTime.UtcNow - triggered).TotalSeconds)).ToString();
                            found = true;
                        }
                        else if (x == "_sincems")
                        {
                            val = ((int)Math.Floor((DateTime.UtcNow - triggered).TotalMilliseconds)).ToString();
                            found = true;
                        }
                        else if (x == "_systemtime")
                        {
                            val = ((long)(DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds).ToString();
                            found = true;
                        }
                        else if (x == "_systemtimems")
                        {
                            val = ((long)(DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0)).TotalMilliseconds).ToString();
                            found = true;
                        }
                        else if (x == "_response")
                        {
                            val = contextResponse;
                            found = true;
                        }
                        else if (x == "_triggername")
                        {
                            val = trig != null ? trig.Name : "(null)";
                            found = true;
                        }
                        else if (x == "_triggerid")
                        {
                            val = trig != null ? trig.Id.ToString() : "(null)";
                            found = true;
                        }
                        else if (x.IndexOf("_jsonresponse") == 0)
                        {
                            mx = rexlidx.Match(x);
                            if (mx.Success == true)
                            {
                                string pathspec = mx.Groups["index"].Value;
                                if (contextJsonParsed == false)
                                {
                                    JavaScriptSerializer jsonSerializer = new JavaScriptSerializer();
                                    contextJsonResponse = jsonSerializer.Deserialize<dynamic>(contextResponse);
                                    contextJsonParsed = true;
                                }
                                string[] path = pathspec.Split("/".ToCharArray());
                                dynamic curdir = contextJsonResponse;
                                foreach (string px in path)
                                {
                                    curdir = curdir[px];
                                }
                                val = curdir.ToString();
                                found = true;
                            }
                        }
                        else if (x.IndexOf("evar:") == 0)
                        {
                            string varname = x.Substring(5);
                            lock (plug.simplevariables) // verified
                            {
                                if (plug.simplevariables.ContainsKey(varname) == true)
                                {
                                    val = "1";
                                }
                                else
                                {
                                    val = "0";
                                }
                                found = true;
                            }
                        }
                        else if (x.IndexOf("elvar:") == 0)
                        {
                            string varname = x.Substring(6);
                            lock (plug.listvariables) // verified
                            {
                                if (plug.listvariables.ContainsKey(varname) == true)
                                {
                                    val = "1";
                                }
                                else
                                {
                                    val = "0";
                                }
                                found = true;
                            }
                        }
                        else if (x.IndexOf("var:") == 0)
                        {
                            string varname = x.Substring(4);
                            lock (plug.simplevariables) // verified
                            {
                                if (plug.simplevariables.ContainsKey(varname) == true)
                                {
                                    val = plug.simplevariables[varname].Value;
                                    found = true;
                                }
                            }
                        }
                        else if (x.IndexOf("lvar:") == 0)
                        {
                            string varname = x.Substring(5);
                            mx = rexlprp.Match(varname);
                            if (mx.Success == true)
                            {
                                string gname = mx.Groups["name"].Value;
                                string gprop = mx.Groups["prop"].Value;
                                switch (gprop)
                                {
                                    case "size":
                                        {
                                            lock (plug.listvariables)
                                            {
                                                VariableList vl = GetListVariable(plug, gname, false);
                                                val = vl.Size().ToString();
                                                found = true;
                                            }
                                        }
                                        break;
                                    case "indexof":
                                        {
                                            string garg = mx.Groups["arg"].Value;
                                            lock (plug.listvariables)
                                            {
                                                VariableList vl = GetListVariable(plug, gname, false);
                                                val = vl.IndexOf(garg).ToString();
                                                found = true;
                                            }
                                        }
                                        break;
                                    case "lastindexof":
                                        {
                                            string garg = mx.Groups["arg"].Value;
                                            lock (plug.listvariables)
                                            {
                                                VariableList vl = GetListVariable(plug, gname, false);
                                                val = vl.LastIndexOf(garg).ToString();
                                                found = true;
                                            }
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                mx = rexlidx.Match(varname);
                                if (mx.Success == true)
                                {
                                    string gname = mx.Groups["name"].Value;
                                    string gindex = mx.Groups["index"].Value;
                                    lock (plug.listvariables)
                                    {
                                        VariableList vl = GetListVariable(plug, gname, false);
                                        if (gindex == "last")
                                        {
                                            gindex = vl.Size().ToString();
                                        }
                                        int iindex;
                                        if (Int32.TryParse(gindex, out iindex) == true)
                                        {
                                            val = vl.Peek(iindex);
                                        }
                                        found = true;
                                    }
                                    found = true;
                                }
                            }                                                            
                        }
                        else if (x.IndexOf("numeric:") == 0)
                        {
                            string numexpr = x.Substring(8);
                            val = I18n.ThingToString(EvaluateNumericExpression(logger, o, numexpr));
                            found = true;
                        }
                        else if (x.IndexOf("string:") == 0)
                        {
                            string numexpr = x.Substring(7);
                            val = EvaluateStringExpression(logger, o, numexpr);
                            found = true;
                        }
                        else if (x.IndexOf("func:") == 0)
                        {
                            val = "";
                            found = true;
                            string funcexpr = x.Substring(5);
                            int splitter = funcexpr.IndexOf(":");
                            if (splitter > 0)
                            {
                                string funcdef = funcexpr.Substring(0, splitter);
                                Match rxm = rexfunc.Match(funcdef);
                                if (rxm.Success == true)
                                {
                                    string funcname = rxm.Groups["name"].Value.ToLower();
                                    string funcarg = rxm.Groups["arg"].Value;
                                    string[] args = SplitArguments(funcarg);
                                    int argc = args.Count();
                                    string funcval = funcexpr.Substring(splitter + 1);
                                    switch (funcname)
                                    {
                                        case "toupper": // toupper()
                                            val = funcval.ToUpper();
                                            break;
                                        case "tolower": // tolower()
                                            val = funcval.ToLower();
                                            break;
                                        case "length": // length()
                                            val = funcval.Length.ToString();
                                            break;
                                        case "hex2dec": // hex2dec()
                                            val = "" + int.Parse(funcval, System.Globalization.NumberStyles.HexNumber);
                                            break;
                                        case "padleft": // padleft(charcode,length)
                                            if (argc != 2)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/padleftargerror", "Padleft function requires two arguments, {0} were given", argc));
                                            }
                                            else
                                            {
                                                val = funcval.PadLeft(Int32.Parse(args[1]), (char)Int32.Parse(args[0]));
                                            }
                                            break;
                                        case "padright": // padright(charcode,length)
                                            if (argc != 2)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/padrightargerror", "Padright function requires two arguments, {0} were given", argc));
                                            }
                                            else
                                            {
                                                val = funcval.PadRight(Int32.Parse(args[1]), (char)Int32.Parse(args[0]));
                                            }
                                            break;
                                        case "substring": // substring(startindex, length) or substring(startindex)
                                            if (argc != 1 && argc != 2)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/substringargerror", "Substring function requires one or two arguments, {0} were given", argc));
                                            }
                                            else
                                            {
                                                switch (argc)
                                                {
                                                    case 1:
                                                        val = funcval.Substring(Int32.Parse(args[0]));
                                                        break;
                                                    case 2:
                                                        val = funcval.Substring(Int32.Parse(args[0]), Int32.Parse(args[1]));
                                                        break;
                                                }
                                            }
                                            break;
                                        case "indexof": // indexof(stringtosearch)
                                            if (argc != 1)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/indexofargerror", "Indexof function requires one argument, {0} were given", argc));
                                            }
                                            else
                                            {
                                                val = "" + funcval.IndexOf(args[0]);
                                            }
                                            break;
                                        case "lastindexof": // lastindexof(stringtosearch)
                                            if (argc != 1)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/lastindexofargerror", "Lastindexof function requires one argument, {0} were given", argc));
                                            }
                                            else
                                            {
                                                val = "" + funcval.LastIndexOf(args[0]);
                                            }
                                            break;
                                        case "trim": // trim() or trim(charcode,charcode,charcode,...)
                                            if (argc == 0 || args[0] == "")
                                            {
                                                val = funcval.Trim();
                                            }
                                            else
                                            {
                                                string xx = "";
                                                foreach (string xyz in args)
                                                {
                                                    xx += (char)Int32.Parse(xyz);
                                                }
                                                val = funcval.Trim(xx.ToCharArray());
                                            }
                                            break;
                                        case "trimleft": // trimleft() or trimleft(charcode,charcode,charcode,...)
                                            if (argc == 0 || args[0] == "")
                                            {
                                                val = funcval.TrimStart();
                                            }
                                            else
                                            {
                                                string xx = "";
                                                foreach (string xyz in args)
                                                {
                                                    xx += (char)Int32.Parse(xyz);
                                                }
                                                val = funcval.TrimStart(xx.ToCharArray());
                                            }
                                            break;
                                        case "trimright": // trimright() or trimright(charcode,charcode,charcode,...)
                                            if (argc == 0 || args[0] == "")
                                            {
                                                val = funcval.TrimEnd();
                                            }
                                            else
                                            {
                                                string xx = "";
                                                foreach (string xyz in args)
                                                {
                                                    xx += (char)Int32.Parse(xyz);
                                                }
                                                val = funcval.TrimEnd(xx.ToCharArray());
                                            }
                                            break;
                                        case "format": // format(type,formatstring)
                                            if (argc != 2)
                                            {
                                                throw new ArgumentException(I18n.Translate("internal/Context/formatargerror", "Format function requires two arguments, {0} were given", argc));
                                            }
                                            else
                                            {
                                                Type type = Type.GetType(args[0]);
                                                object converted = Convert.ChangeType(funcval, type, CultureInfo.InvariantCulture);
                                                val = String.Format("{0:" + args[1] + "}", converted);
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                        else if (x == "_ffxivplayer")
                        {
                            VariableClump vc = PluginBridges.BridgeFFXIV.GetMyself();
                            if (vc != null)
                            {
                                val = vc.GetValue("name");
                            }
                            found = true;
                        }
                        else if (x == "_ffxivpartyorder")
                        {
                            val = plug.cfg.FfxivPartyOrdering + " " + plug.cfg.FfxivCustomPartyOrder;
                            found = true;
                        }
                        else if (x == "_incombat")
                        {
                            val = plug != null && plug.InCombatHook() ? "1" : "0";
                            found = true;
                        }
                        else if (x == "_duration")
                        {
                            try
                            {
                                if (plug != null && plug.InCombatHook() == true)
                                {
                                    val = ((int)Math.Floor(plug.EncounterDurationHook())).ToString();
                                }
                                else
                                {
                                    val = "0";
                                }
                            }
                            catch (Exception)
                            {
                                val = "0";
                            }
                            found = true;
                        }
                        else if (x.IndexOf("_ffxivparty") == 0)
                        {
                            mx = rexnump.Match(x);
                            if (mx.Success == true)
                            {
                                string gindex = mx.Groups["index"].Value;
                                string gprop = mx.Groups["prop"].Value;
                                int iindex = 0;
                                Int64 honk;
                                VariableClump vc = null;
                                bool foundid = false;
                                if (Int64.TryParse(gindex, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out honk) == true)
                                {
                                    vc = PluginBridges.BridgeFFXIV.GetIdPartyMember(gindex, out foundid);
                                }                                
                                if (foundid == false)
                                {
                                    if (Int32.TryParse(gindex, out iindex) == true)
                                    {
                                        vc = PluginBridges.BridgeFFXIV.GetPartyMember(iindex);
                                    }
                                    else
                                    {
                                        vc = PluginBridges.BridgeFFXIV.GetNamedPartyMember(gindex);
                                    }
                                }
                                if (vc != null)
                                {
                                    val = vc.GetValue(gprop);
                                }
                            }
                            found = true;
                        }
                        else if (x.IndexOf("_ffxiventity") == 0)
                        {
                            mx = rexnump.Match(x);
                            if (mx.Success == true)
                            {
                                string gindex = mx.Groups["index"].Value;
                                string gprop = mx.Groups["prop"].Value;
                                Int64 honk;
                                VariableClump vc = null;
                                bool foundid = false;
                                if (Int64.TryParse(gindex, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out honk) == true)
                                {
                                    vc = PluginBridges.BridgeFFXIV.GetIdEntity(gindex, out foundid);
                                }
                                if (foundid == false)
                                {
                                    vc = PluginBridges.BridgeFFXIV.GetNamedEntity(gindex);
                                }
                                if (vc != null)
                                {
                                    val = vc.GetValue(gprop);
                                }
                            }
                            found = true;
                        }
                        else if (x == "_ffxivtime")
                        {
                            TimeSpan ez = GetEorzeanTime();
                            int mins = (int)Math.Floor(ez.TotalMinutes);
                            val = mins.ToString();
                            found = true;
                        }
                        else if (x == "_lastencounter")
                        {
                            val = plug != null ? plug.LastEncounterHook() : "";
                            found = true;
                        }
                        else if (x == "_activeencounter")
                        {
                            val = plug != null ? plug.ActiveEncounterHook() : "";
                            found = true;
                        }
                        else if (x.IndexOf("_textaura") == 0)
                        {
                            mx = rexnump.Match(x);
                            if (mx.Success == true)
                            {
                                string gindex = mx.Groups["index"].Value;
                                string gprop = mx.Groups["prop"].Value.ToLower();
                                val = "";
                                if (plug.sc != null)
                                {
                                    lock (plug.sc.textitems)
                                    {
                                        Scarborough.ScarboroughText item = plug.sc.GetText(gindex);
                                        if (item != null)
                                        {
                                            switch (gprop)
                                            {
                                                case "x":
                                                    val = item.Left.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "y":
                                                    val = item.Top.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "w":
                                                    val = item.Width.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "h":
                                                    val = item.Height.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "opacity":
                                                    val = item.Opacity.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "text":
                                                    val = item.Text;
                                                    break;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    lock (plug.textauras)
                                    {
                                        if (plug.textauras.ContainsKey(gindex) == true)
                                        {
                                            Forms.AuraContainerForm acf = plug.textauras[gindex];
                                            switch (gprop)
                                            {
                                                case "x":
                                                    val = acf.Left.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "y":
                                                    val = acf.Top.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "w":
                                                    val = acf.Width.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "h":
                                                    val = acf.Height.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "opacity":
                                                    val = acf.PresentableOpacity.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "text":
                                                    val = acf.CurrentText;
                                                    break;
                                            }
                                        }
                                    }
                                }
                                found = true;
                            }
                        }
                        else if (x.IndexOf("_imageaura") == 0)
                        {
                            mx = rexnump.Match(x);
                            if (mx.Success == true)
                            {
                                string gindex = mx.Groups["index"].Value;
                                string gprop = mx.Groups["prop"].Value.ToLower();
                                val = "";
                                if (plug.sc != null)
                                {
                                    lock (plug.sc.imageitems)
                                    {
                                        Scarborough.ScarboroughImage item = plug.sc.GetImage(gindex);
                                        if (item != null)
                                        {
                                            switch (gprop)
                                            {
                                                case "x":
                                                    val = item.Left.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "y":
                                                    val = item.Top.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "w":
                                                    val = item.Width.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "h":
                                                    val = item.Height.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "opacity":
                                                    val = item.Opacity.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    lock (plug.imageauras)
                                    {
                                        if (plug.imageauras.ContainsKey(gindex) == true)
                                        {
                                            Forms.AuraContainerForm acf = plug.imageauras[gindex];
                                            switch (gprop)
                                            {
                                                case "x":
                                                    val = acf.Left.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "y":
                                                    val = acf.Top.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "w":
                                                    val = acf.Width.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "h":
                                                    val = acf.Height.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                                case "opacity":
                                                    val = acf.PresentableOpacity.ToString(CultureInfo.InvariantCulture);
                                                    break;
                                            }
                                        }
                                    }
                                }
                                found = true;
                            }
                        }
                        else if (x == "_screenwidth")
                        {
                            System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.PrimaryScreen;
                            val = scr.WorkingArea.Width.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                        else if (x == "_screenheight")
                        {
                            System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.PrimaryScreen;
                            val = scr.WorkingArea.Height.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                        else if (x == "_screenminx")
                        {                            
                            val = plug.MinX.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                        else if (x == "_screenminy")
                        {
                            val = plug.MinY.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                        else if (x == "_screenmaxx")
                        {
                            val = plug.MaxX.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                        else if (x == "_screenmaxy")
                        {
                            val = plug.MaxY.ToString(CultureInfo.InvariantCulture);
                            found = true;
                        }
                    }
                }
                newexpr = newexpr.Substring(0, m.Index) + val + newexpr.Substring(m.Index + m.Length);
                /*
                if (logger != null)
                {
                    logger(o, I18n.Translate("internal/Context/expansionstep", "Expansion step {0} from '{1}' to '{2}' ({3}) = '{4}'", i, expr, newexpr, val, found));
                }
                else if (plug != null)
                {
                    plug.FilteredAddToLog(Plugin.DebugLevelEnum.Verbose, I18n.Translate("internal/Context/expansionstep", "Expansion step {0} from '{1}' to '{2}' ({3}) = '{4}'", i, expr, newexpr, val, found));
                }*/
                i++;
            };
            if (trig != null)
            {
                if (trig.DebugLevel == RealPlugin.DebugLevelEnum.Inherit)
                {
                    if (plug != null)
                    {
                        if (plug.cfg.DebugLevel < RealPlugin.DebugLevelEnum.Verbose)
                        {
                            return newexpr;
                        }
                    }
                }
                else
                {
                    if (trig.DebugLevel < RealPlugin.DebugLevelEnum.Verbose)
                    {
                        return newexpr;
                    }
                }
            }
            if (newexpr.CompareTo(expr) != 0)
            {
                if (logger != null)
                {
                    logger(o, I18n.Translate("internal/Context/expansion", "Variable expansion from '{0}' to '{1}'", expr, newexpr));
                }
                else if (plug != null)
                {
                    plug.FilteredAddToLog(RealPlugin.DebugLevelEnum.Verbose, I18n.Translate("internal/Context/expansion", "Variable expansion from '{0}' to '{1}'", expr, newexpr));
                }
            }
            return newexpr;
        }

    }

}

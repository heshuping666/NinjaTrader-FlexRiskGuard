using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class P0Account
    {
        public string Name;
        public bool ManualLock;
        public bool NeedsResume;
        public DateTime UntilUtc;
    }
    public class P0State
    {
        public int Version = 1;
        public bool GlobalLock;
        public DateTime Session;
        public int Violations;
        public bool Ended;
        public int CooldownMinutes = 30;
        public List<P0Account> Accounts = new List<P0Account>();
    }
    public class P0Store
    {
        private static readonly object Sync = new object();
        private readonly string path;
        public P0Store() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "FlexRiskGuard50K", "behavior-v1.xml")) { }
        public P0Store(string file) { path = file; }
        private P0State Read()
        {
            if (!File.Exists(path)) return new P0State();
            P0State state;
            using (var stream = File.OpenRead(path)) state = (P0State)new XmlSerializer(typeof(P0State)).Deserialize(stream);
            if (state.Version != 1 || state.Accounts == null || state.Violations < 0) throw new InvalidDataException("P0 状态文件无效");
            return state;
        }
        private static DateTime Day { get { return DateTime.UtcNow.AddHours(2).Date; } }
        private static P0Account Account(P0State state, string name)
        {
            if (String.IsNullOrEmpty(name) || !name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("仅支持 Sim 模拟账户");
            var account = state.Accounts.SingleOrDefault(a => a.Name == name);
            if (account == null) { account = new P0Account { Name = name }; state.Accounts.Add(account); }
            return account;
        }
        public string Status(string name)
        {
            lock (Sync)
            {
                try
                {
                    var s = Read(); var a = Account(s, name);
                    if (s.GlobalLock) return "全局手动锁定";
                    if (s.Session == Day && s.Ended) return "全局当日锁定：结束交易 / 第二次违规";
                    // Cooldown applies across all managed accounts and survives switching.
                    var pending = s.Accounts.Where(x => x.NeedsResume).ToList();
                    if (pending.Any(x => x.UntilUtc > DateTime.UtcNow)) return "全局冷静期：" + Countdown(name);
                    if (pending.Count > 0) return "冷静期结束：需要手动恢复";
                    return a.ManualLock ? "账户手动锁定" : "内部未锁定 · 仅监控";
                }
                catch (Exception ex) { return "P0 状态不可靠：" + ex.Message; }
            }
        }
        public string Countdown(string name)
        {
            lock (Sync)
            {
                try { var s = Read(); Account(s, name); var pending = s.Accounts.Where(a => a.NeedsResume).ToList();
                    if (pending.Count == 0) return "—";
                    var remaining = pending.Max(a => a.UntilUtc) - DateTime.UtcNow;
                    return remaining.TotalSeconds > 0 ? Math.Ceiling(remaining.TotalMinutes) + " 分钟" : "待恢复";
                } catch { return "未知"; }
            }
        }
        public void Change(string name, string action, int minutes)
        {
            lock (Sync)
            {
                if (minutes < 1 || minutes > 1440) throw new ArgumentOutOfRangeException("minutes");
                var s = Read(); var a = Account(s, name);
                if (s.Session != Day) { s.Session = Day; s.Violations = 0; s.Ended = false; }
                s.CooldownMinutes = minutes;
                switch (action)
                {
                    case "lock": a.ManualLock = true; break;
                    case "globalLock": s.GlobalLock = true; break;
                    case "globalResume": s.GlobalLock = false; break;
                    case "endDay": s.Ended = true; break;
                    case "violation": s.Violations++; if (s.Violations >= 2) s.Ended = true;
                        a.NeedsResume = true; a.UntilUtc = DateTime.UtcNow.AddMinutes(minutes); break;
                    case "resume":
                        if (s.Accounts.Any(x => x.NeedsResume && x.UntilUtc > DateTime.UtcNow)) throw new InvalidOperationException("冷静期尚未结束");
                        if (s.Ended) throw new InvalidOperationException("当日锁定不能恢复");
                        a.ManualLock = false;
                        foreach (var item in s.Accounts) item.NeedsResume = false;
                        break;
                    default: throw new ArgumentException("未知操作");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                { new XmlSerializer(typeof(P0State)).Serialize(stream, s); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
        }
    }
}


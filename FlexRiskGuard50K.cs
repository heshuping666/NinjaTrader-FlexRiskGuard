// FlexRiskGuard50K.cs  |  NT8 NinjaScript AddOn  |  SIM-ONLY evaluation build
// IMPORTANT: OrderUpdate happens AFTER submission. Cancellation is BEST EFFORT.
// This AddOn is NOT a broker-side risk limit or a guaranteed pre-trade gate.
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class FlexRiskGuard50K : AddOnBase
    {
        private NTMenuItem menu;
        private NTMenuItem newMenu;
        private Window panel;
        private ComboBox accounts;
        private TextBox mllInput, stopInput, qtyInput;
        private ComboBox instrumentInput;
        private TextBlock status, figures, preflight;
        private readonly List<TextBlock> accountCells = new List<TextBlock>();

        private Grid BuildAccountTable()
        {
            accountCells.Clear();
            Grid table = new Grid { Background = Brushes.White, MinWidth = 770 };
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            string[] headings = { "状态", "账户", "日内盈亏 $", "权益 $", "MLL $", "缓冲 $", "入场", "冷静期" };
            for (int i = 0; i < headings.Length; i++)
            {
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 1 ? 130 : 95) });
                for (int row = 0; row < 2; row++)
                {
                    TextBlock text = new TextBlock { Text = row == 0 ? headings[i] : "—",
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0),
                        Foreground = Brushes.Black, FontSize = 12,
                        FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal };
                    Border cell = new Border { Child = text, BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Background = row == 0 ? Brushes.WhiteSmoke : Brushes.White };
                    Grid.SetColumn(cell, i); Grid.SetRow(cell, row); table.Children.Add(cell);
                    if (row == 1) accountCells.Add(text);
                }
            }
            return table;
        }

        private void ClearAccountTable()
        {
            foreach (TextBlock cell in accountCells) { cell.Text = "—"; cell.ToolTip = null; }
        }
        private DispatcherTimer timer;
        private Account account;
        private string currentAccountName = "";
        private bool locked = true;
        private string reason = "尚未初始化";
        private DateTime tradingDate = DateTime.MinValue;
        private DateTime pauseUntil = DateTime.MinValue;
        private double realizedStart;
        private int entries;
        private double manualMll = double.NaN;
        private HashSet<string> seenEntryOrders = new HashSet<string>();
        private HashSet<string> seenExecutions = new HashSet<string>();
        private HashSet<Order> cancellationRequested = new HashSet<Order>();
        private readonly Dictionary<Order, string> cancellationReasons = new Dictionary<Order, string>();
        private readonly object guard = new object();
        private readonly object logGuard = new object();
        private readonly Queue<string> logLines = new Queue<string>();
        private TextBox logView;
        private TextBlock logSummary;
        private long logVersion, renderedLogVersion = -1;
        private string latestRiskAction = "本次会话尚无风控操作";
        private readonly HashSet<Order> reportedOrderResults = new HashSet<Order>();
        private string lastMetricsError = "";
        private bool dailyLossTripped;
        private string stateError = "";
        private bool refreshingAccounts;

        // Fixed UTC+8, 06:00 boundary. Session label is its starting Beijing date.
        private static DateTime SessionDate(DateTime utc)
        {
            return utc.AddHours(8).AddHours(-6).Date;
        }
        private static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }

        private readonly P0Store behavior = new P0Store();
        private TextBox cooldownMinutes;
        private void BehaviorAction(string action)
        {
            if (account == null) return;
            try
            {
                int minutes;
                if (!Int32.TryParse(cooldownMinutes.Text, out minutes) || minutes < 1 || minutes > 1440)
                    throw new InvalidOperationException("冷静期需为 1–1440 分钟");
                if ((action == "resume" || action == "globalResume") && MessageBox.Show(
                    "确认恢复插件内部状态？本插件不会阻断 NT8 订单。", "恢复确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                behavior.Change(account.Name, action, minutes);
                LogEvent("P0 操作", action + "；状态已保存", true);
            }
            catch (Exception ex) { LogEvent("P0 操作失败", ex.Message, true); }
            RefreshMetrics(); RefreshLog();
        }

        private void EvaluateRisk()
        {
            Account a = account;
            if (a == null) return;
            double r = a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            double u = a.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
            double n = a.Get(AccountItem.NetLiquidation, Currency.UsDollar);
            DateTime session = SessionDate(DateTime.UtcNow);
            bool reset = false, save = false, changed;
            string next;
            bool flat = !HasPosition(a) && !HasActiveOrder(a);
            lock (guard)
            {
                if (stateError == "" && tradingDate < session && flat && Finite(r) && Finite(u) && Valid(n))
                {
                    tradingDate = session; realizedStart = r; entries = 0;
                    dailyLossTripped = false;
                    seenEntryOrders.Clear(); seenExecutions.Clear();
                    reset = true; save = true;
                }
                if (tradingDate == session && Finite(r) && Finite(u) &&
                    r - realizedStart + u <= -DailyLossCap && !dailyLossTripped)
                { dailyLossTripped = true; save = true; }
                next = behavior.Status(a.Name);
                if (stateError != "") next = stateError;
                if (!Finite(r) || !Finite(u) || !Valid(n)) next = "数据不可靠：账户数值不可用";
                var blockers = new List<string>();
                if (next != "内部未锁定 · 仅监控") blockers.Add(next);
                bool nextLocked = blockers.Count > 0;
                changed = locked != nextLocked || reason != next;
                locked = nextLocked; reason = next;
            }
            if (reset) LogEvent("自动换日", "交易日 " + session.ToString("yyyy-MM-dd") + "；北京时间 06:00 切换，已建立盈亏基准并清零入场次数。", true);
            if (save) SaveState();
            if (changed) LogEvent(locked ? "禁止新开仓" : "允许交易", reason, true);
        }

        private void LogEvent(string category, string message, bool riskAction = false)
        {
            lock (logGuard)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ["
                    + (String.IsNullOrEmpty(currentAccountName) ? "未绑定" : currentAccountName)
                    + "] [" + category + "] " + message;
                logLines.Enqueue(line);
                while (logLines.Count > 500) logLines.Dequeue();
                if (riskAction) latestRiskAction = line;
                logVersion++;
            }
        }

        // Account callbacks only enqueue text; the UI timer renders it on its dispatcher.
        private void RefreshLog()
        {
            if (logView == null) return;
            lock (logGuard)
            {
                if (renderedLogVersion == logVersion) return;
                logView.Text = String.Join(Environment.NewLine, logLines.ToArray());
                logSummary.Text = "最近风控操作：" + latestRiskAction;
                logSummary.ToolTip = logSummary.Text;
                renderedLogVersion = logVersion;
            }
            logView.ScrollToEnd();
        }

        // SIM research defaults. These are NOT official FundedNext limits.
        private const double MaxSingleRisk = 150.0;
        private const double MaxCombinedRisk = 200.0; // advisory only; existing stops not reliably reconstructed
        private const double DailyLossCap = 300.0;
        private const int EntryCap = 5;
        private const double BufferFloor = 500.0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FlexRiskGuard50K";
                Description = "SIM ONLY: P0 internal lock state monitor; no order control";
            }
            else if (State == State.Terminated)
            {
                Detach();
                if (timer != null) timer.Stop();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null) return;
            newMenu = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (newMenu == null) return;
            menu = new NTMenuItem
            {
                Header = "Flex Risk Guard 50K (SIM)",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            menu.Click += (s, e) => OpenPanel();
            newMenu.Items.Add(menu);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (!(window is ControlCenter) || menu == null) return;
            if (newMenu != null) newMenu.Items.Remove(menu);
            menu = null;
            newMenu = null;
        }

        private void OpenPanel()
        {
            if (panel != null)
            {
                panel.Show();
                panel.Activate();
                return;
            }

            panel = new Window
            {
                Title = "Flex Risk Guard 50K — SIM ONLY",
                Width = 1180,
                Height = 760,
                MinWidth = 900,
                MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White
            };
            ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel body = new StackPanel { Margin = new Thickness(8) };
            scroller.Content = body;
            Grid layout = new Grid { Margin = new Thickness(10) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(scroller);
            Grid right = new Grid { Margin = new Thickness(16, 8, 8, 8) };
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);
            StackPanel overview = new StackPanel();
            right.Children.Add(new ScrollViewer { Content = overview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Grid logArea = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            logArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            logArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(logArea, 1);
            right.Children.Add(logArea);
            logArea.Children.Add(Label("操作日志 · 本次会话最近 500 条", true));
            logSummary = Label("", false);
            logSummary.TextWrapping = TextWrapping.NoWrap;
            logSummary.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(logSummary, 1);
            logArea.Children.Add(logSummary);
            logView = new TextBox { IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.WhiteSmoke, Foreground = Brushes.Black,
                FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(10) };
            Grid.SetRow(logView, 2);
            logArea.Children.Add(logView);
            renderedLogVersion = -1;
            panel.Content = layout;

            body.Children.Add(Label("Flex Risk Guard · SIM", true));
            body.Children.Add(Label("模拟账户", true));
            accounts = new ComboBox { Height = 30, Margin = new Thickness(0, 4, 0, 10) };
            body.Children.Add(accounts);
            Button refresh = Btn("刷新账户列表", () => RefreshAccountList());
            refresh.ToolTip = "仅刷新可选模拟账户，不重置冷静期或风控统计。";
            body.Children.Add(refresh);
            accounts.SelectionChanged += (s, e) => { if (!refreshingAccounts) SelectAccount(); };

            body.Children.Add(Label("MLL · USD", true));
            mllInput = new TextBox { Height = 28, Text = "", Margin = new Thickness(0, 4, 0, 5) };
            body.Children.Add(mllInput);
            body.Children.Add(Btn("保存当前 MLL", SaveMll));

            status = Label("未绑定账户", true);
            status.Foreground = Brushes.DarkRed;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 15, 0, 8);
            overview.Children.Add(Label("账户监控", true));
            overview.Children.Add(status);
            figures = Label("", false);
            figures.TextWrapping = TextWrapping.Wrap;
            overview.Children.Add(new ScrollViewer { Content = BuildAccountTable(),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Expander details = new Expander { Header = "锁定原因 / 状态详情", Content = figures,
                Margin = new Thickness(0, 8, 0, 0) };
            overview.Children.Add(details);
            body.Children.Add(Label("风控操作", true));

            body.Children.Add(Label("自动检查 · 北京时间 06:00 换日", false));
            cooldownMinutes = new TextBox { Text = "30", Height = 28 };
            body.Children.Add(Label("冷静期（分钟）", false));
            body.Children.Add(cooldownMinutes);
            body.Children.Add(Btn("锁定当前账户", () => BehaviorAction("lock")));
            body.Children.Add(Btn("恢复当前账户", () => BehaviorAction("resume")));
            body.Children.Add(Btn("记录违规 / 冷静期", () => BehaviorAction("violation")));
            body.Children.Add(Btn("结束当日交易", () => BehaviorAction("endDay")));
            body.Children.Add(Btn("锁定全部模拟账户", () => BehaviorAction("globalLock")));
            body.Children.Add(Btn("恢复全局锁定", () => BehaviorAction("globalResume")));
            body.Children.Add(Label("P0 仅记录内部状态，不撤单、不平仓、不阻断下单。", true));

            body.Children.Add(Label("风险计算 · 不下单", true));
            instrumentInput = new ComboBox { Height = 28, Margin = new Thickness(0, 4, 0, 5) };
            instrumentInput.Items.Add("MNQ"); instrumentInput.Items.Add("MES"); instrumentInput.Items.Add("MGC");
            instrumentInput.SelectedIndex = 0;
            body.Children.Add(instrumentInput);
            body.Children.Add(Label("止损距离（点 / MGC 价格美元）", false));
            stopInput = new TextBox { Text = "75", Height = 28 };
            body.Children.Add(stopInput);
            body.Children.Add(Label("手数：", false));
            qtyInput = new TextBox { Text = "1", Height = 28 };
            body.Children.Add(qtyInput);
            body.Children.Add(Btn("计算计划风险", ComputePreflight));
            preflight = Label("", false);
            preflight.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(preflight);
            body.Children.Add(new Expander { Header = "规则与使用说明", Margin = new Thickness(0, 10, 0, 0), Content = Label("交易日按北京时间 06:00 自动切换；跨日待空仓、无活动订单后重置统计。锁定不平仓；关闭面板会停止监控。\n注意：单笔 $150、合计 $200、日亏 $300、3 笔、缓冲 $500 均是研究参数。\n"
                + "监控不计算手续费/滑点；不能从普通 Chart Trader 下单前确认你的结构止损。\n"
                + "P0 仅管理内部锁定状态，不提交、不撤销、不平仓任何订单。", false) });

            // Closing the window MUST NOT silently lift a lock.
            panel.Closed += (s, e) =>
            {
                if (timer != null) { timer.Stop(); timer = null; }
                Detach();
                panel = null;
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => { RefreshMetrics(); RefreshLog(); };
            timer.Start();
            RefreshAccountList();
            LogEvent("界面", "面板已打开；日志仅保留本次程序会话，不写入磁盘。");
            RefreshLog();
            panel.Show();
        }

        private static TextBlock Label(string content, bool title)
        {
            return new TextBlock
            {
                Text = content,
                FontSize = title ? 14 : 12,
                FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, title ? 9 : 5, 0, 4),
                Foreground = title ? Brushes.Black : Brushes.DimGray
                , TextWrapping = TextWrapping.Wrap
            };
        }

        private static Button Btn(string content, Action clicked)
        {
            Button b = new Button { Content = content, Margin = new Thickness(0, 3, 0, 3), Height = 32 };
            b.Click += (s, e) => clicked();
            return b;
        }

        private void RefreshAccountList()
        {
            string previouslySelected = accounts.SelectedItem as string;
            var names = new List<string>();
            lock (Account.All)
                names = Account.All.Where(a => a.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Name).OrderBy(x => x).ToList();
            refreshingAccounts = true;
            accounts.Items.Clear();
            foreach (string name in names) accounts.Items.Add(name);
            if (names.Count > 0)
                accounts.SelectedItem = names.Contains(previouslySelected) ? previouslySelected : names[0];
            refreshingAccounts = false;
            SelectAccount();
        }

        private void SelectAccount()
        {
            string name = accounts.SelectedItem as string;
            if (name == currentAccountName) return;
            Detach();
            if (String.IsNullOrEmpty(name)) { RefreshMetrics(); return; }
            lock (Account.All) account = Account.All.FirstOrDefault(a => a.Name == name);
            if (account == null || !account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase))
                { account = null; return; }
            currentAccountName = name;
            LoadState();
            // Stay blocked until current settings and persisted risk state have been evaluated.
            lock (guard)
            {
                locked = true;
                reason = "正在自动检查风控条件";
            }
            SaveState();
            account.ExecutionUpdate += OnExecutionUpdate;
            // P0: no order cancellation subscription.
            account.AccountItemUpdate += OnAccountItemUpdate;
            LogEvent("监控", "已订阅账户事件；自动检查风控条件。", true);
            mllInput.Text = Double.IsNaN(manualMll) ? "" : manualMll.ToString("F2", CultureInfo.InvariantCulture);
            RefreshMetrics();
        }

        private void Detach()
        {
            if (account != null)
            {
                LogEvent("监控停止", "已解除账户事件订阅；此账户不再受本面板监控。");
                account.ExecutionUpdate -= OnExecutionUpdate;

                account.AccountItemUpdate -= OnAccountItemUpdate;
            }
            account = null;
            currentAccountName = "";
        }

        private string StateFile()
        {
            string safe = new string(currentAccountName.Select(c => Char.IsLetterOrDigit(c) ? c : '_').ToArray());
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "FlexRiskGuard50K");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, safe + ".txt");
        }

        private void LoadState()
        {
            lock (guard)
            {
                stateError = ""; dailyLossTripped = false;
                locked = true;
                tradingDate = DateTime.MinValue;
                realizedStart = 0;
                entries = 0;
                manualMll = double.NaN;
                seenEntryOrders.Clear();
                seenExecutions.Clear();
                cancellationRequested.Clear();
                cancellationReasons.Clear();
                reportedOrderResults.Clear();
                pauseUntil = DateTime.MinValue;
                string path = StateFile();
                if (!File.Exists(path)) return;
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length < 5) throw new InvalidDataException("状态文件不完整");
                    tradingDate = DateTime.Parse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    realizedStart = double.Parse(lines[1], CultureInfo.InvariantCulture);
                    entries = int.Parse(lines[2], CultureInfo.InvariantCulture);
                    double x;
                    if (double.TryParse(lines[3], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && Valid(x))
                        manualMll = x;
                    pauseUntil = DateTime.Parse(lines[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    dailyLossTripped = lines.Contains("DAILY_LOSS=TRUE");
                    if (!Finite(realizedStart) || entries < 0) throw new InvalidDataException("无效统计数据");
                    // Old files used PC calendar dates; retain restrictions and baseline conservatively.
                    if (!lines.Contains("SESSION=BEIJING_0600_V1")) tradingDate = SessionDate(DateTime.UtcNow);
                }
                catch (Exception) { locked = true; stateError = "状态文件异常，需修复后重新加载"; reason = stateError; }
            }
        }

        private void SaveState()
        {
            if (String.IsNullOrEmpty(currentAccountName) || stateError != "") return;
            try
            {
                string data;
                lock (guard)
                    data = tradingDate.ToString("o", CultureInfo.InvariantCulture) + "\n"
                        + realizedStart.ToString("R", CultureInfo.InvariantCulture) + "\n"
                        + entries.ToString(CultureInfo.InvariantCulture) + "\n"
                        + manualMll.ToString("R", CultureInfo.InvariantCulture) + "\n"
                        + pauseUntil.ToString("o", CultureInfo.InvariantCulture) + "\n"
                        + "SESSION=BEIJING_0600_V1\n"
                        + (dailyLossTripped ? "DAILY_LOSS=TRUE\n" : "DAILY_LOSS=FALSE\n");
                File.WriteAllText(StateFile(), data, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Do not mistake an unwritable file for a working persistent lock.
                lock (guard) { locked = true; stateError = "状态保存失败：" + ex.Message; reason = stateError; }
                LogEvent("错误", reason, true);
            }
        }

        private static bool HasActiveOrder(Account a)
        {
            lock (a.Orders)
                return a.Orders.Any(o => !Order.IsTerminalState(o.OrderState));
        }
        private static bool HasPosition(Account a)
        {
            lock (a.Positions)
                return a.Positions.Any(p => p.MarketPosition != MarketPosition.Flat && p.Quantity != 0);
        }

        private void SaveMll()
        {
            if (account == null) return;
            double x;
            if (!double.TryParse(mllInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !Valid(x))
            { MessageBox.Show("输入有效的 USD MLL，例如 48500。必须与选定模拟账户权益相匹配。"); return; }
            lock (guard) manualMll = x;
            LogEvent("设置", "MLL 已设置为 " + x.ToString("F2", CultureInfo.InvariantCulture) + " USD");
            SaveState(); RefreshMetrics();
        }

        private static bool Valid(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x) && x > 0; }

        private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            if (panel != null) panel.Dispatcher.BeginInvoke(new Action(RefreshMetrics));
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e.Execution == null || account == null) return;
            string executionId = e.Execution.ExecutionId;
            if (String.IsNullOrEmpty(executionId)) return;
            bool entry = e.Execution.Order != null &&
                (e.Execution.Order.OrderAction == OrderAction.Buy ||
                 e.Execution.Order.OrderAction == OrderAction.SellShort);
            string orderKey = e.Execution.OrderId;
            bool changed = false;
            lock (guard)
            {
                if (!seenExecutions.Add(executionId)) return;
                if (entry && !String.IsNullOrEmpty(orderKey) && seenEntryOrders.Add(orderKey))
                { entries++; changed = true; }
            }
            if (changed)
            {
                LogEvent("入场计数", "订单 " + orderKey + "，当前计数 " + entries + "/" + EntryCap);
                SaveState();
            }
            if (panel != null) panel.Dispatcher.BeginInvoke(new Action(RefreshMetrics));
        }

        private void RefreshMetrics()
        {
            if (status == null || figures == null) return;
            if (account == null) { status.Text = "● 未绑定"; status.Foreground = Brushes.Gray; status.ToolTip = null; figures.Text = "请选择模拟账户。"; ClearAccountTable(); return; }
            double r, u, nlv;
            try
            {
                EvaluateRisk();
                r = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                u = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                nlv = account.Get(AccountItem.NetLiquidation, Currency.UsDollar);
            }
            catch (Exception ex)
            {
                if (lastMetricsError != ex.Message) LogEvent("账户读取错误", ex.Message + "；已锁定。", true);
                lastMetricsError = ex.Message;
                lock (guard) { locked = true; reason = "无法读取账户数值：" + ex.Message; }
                SaveState();
                status.Text = "已锁定 | " + reason;
                status.Foreground = Brushes.DarkRed;
                figures.Text = "账户数值不可用；请检查连接。";
                ClearAccountTable();
                return;
            }
            if (lastMetricsError != "") LogEvent("账户读取恢复", "已恢复读取；重新自动检查条件。");
            lastMetricsError = "";
            double baseline; int count; bool isLocked; DateTime date, until; string why; double mll;
            lock (guard)
            { baseline = realizedStart; count = entries; isLocked = locked;
              date = tradingDate; until = pauseUntil; why = reason; mll = manualMll; }
            double sessionPnl = (r - baseline) + u;
            double buffer = nlv - mll;
            status.Text = isLocked ? "● 内部锁定 · 订单未阻断" : "● 内部未锁定 · 仅监控";
            status.ToolTip = why;
            status.Foreground = isLocked ? Brushes.DarkRed : Brushes.DarkGreen;
            string[] values = { isLocked ? "● 锁定" : "● 未锁定", account.Name,
                date == SessionDate(DateTime.UtcNow) ? sessionPnl.ToString("F2") : "未初始化",
                nlv.ToString("F2"), Double.IsNaN(mll) ? "未设置" : mll.ToString("F2"),
                Double.IsNaN(mll) ? "—" : buffer.ToString("F2"), count + "/" + EntryCap,
                behavior.Countdown(account.Name) };
            for (int i = 0; i < values.Length; i++)
            { accountCells[i].Text = values[i]; accountCells[i].ToolTip = values[i]; }
            accountCells[0].Foreground = status.Foreground;
            accountCells[0].ToolTip = why;
            accountCells[2].Foreground = date != SessionDate(DateTime.UtcNow) ? Brushes.Gray : sessionPnl < 0 ? Brushes.DarkRed : Brushes.DarkGreen;
            accountCells[5].Foreground = buffer < BufferFloor ? Brushes.DarkRed : Brushes.Black;
            figures.Text = why + "\nP1 数据未核对：盈亏和入场为近似统计，无法重建离线成交；不参与自动订单控制。";
        }

        private void ComputePreflight()
        {
            double distance; int qty;
            if (!double.TryParse(stopInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out distance) ||
                !int.TryParse(qtyInput.Text, out qty) || !Valid(distance) || qty <= 0)
            { preflight.Text = "输入正数止损距离、正整数手数；使用英文小数点。"; return; }
            string sym = instrumentInput.SelectedItem as string;
            double pointValue = sym == "MNQ" ? 2 : sym == "MES" ? 5 : 10;
            double risk = distance * pointValue * qty;
            double availableBuffer = account != null && !Double.IsNaN(manualMll)
                ? account.Get(AccountItem.NetLiquidation, Currency.UsDollar) - manualMll : double.NaN;
            double graduated = availableBuffer >= 1000 ? 150 : availableBuffer >= 700 ? 100 : availableBuffer >= 500 ? 50 : 0;
            double limit = Math.Min(MaxSingleRisk, graduated);
            preflight.Text = String.Format(CultureInfo.InvariantCulture,
                "计划价格风险 ${0:F2}（未含手续费/滑点）；当前分级上限 ${1:F2}。\n{2}\n注意：这不是订单预审批；请另行检查同账户已持仓风险和当日余量。",
                risk, limit, risk <= limit && limit > 0 ? "通过单笔价格风险检查" : "不满足单笔风险限制");
        }
    }
}



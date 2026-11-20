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
        private readonly object guard = new object();

        // SIM research defaults. These are NOT official FundedNext limits.
        private const double MaxSingleRisk = 150.0;
        private const double MaxCombinedRisk = 200.0; // advisory only; existing stops not reliably reconstructed
        private const double DailyLossCap = 300.0;
        private const int EntryCap = 3;
        private const double BufferFloor = 500.0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FlexRiskGuard50K";
                Description = "SIM ONLY: 50K Flex risk monitor and best-effort locked-entry cancellation";
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
                Width = 555,
                Height = 680,
                MinWidth = 505,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White
            };
            ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            StackPanel body = new StackPanel { Margin = new Thickness(18) };
            scroller.Content = body;
            panel.Content = scroller;

            body.Children.Add(Label("SIM-ONLY  |  仅供模拟验证，不能保证拦截已提交订单", true));
            body.Children.Add(Label("账户（仅显示名称以 Sim 开头的账户）：", false));
            accounts = new ComboBox { Height = 30, Margin = new Thickness(0, 4, 0, 10) };
            body.Children.Add(accounts);
            Button refresh = Btn("刷新模拟账户", () => RefreshAccountList());
            body.Children.Add(refresh);
            accounts.SelectionChanged += (s, e) => SelectAccount();

            body.Children.Add(Label("当前生效的 Maximum Loss Limit（手动输入，USD）：", false));
            mllInput = new TextBox { Height = 28, Text = "", Margin = new Thickness(0, 4, 0, 5) };
            body.Children.Add(mllInput);
            body.Children.Add(Btn("保存当前 MLL", SaveMll));

            status = Label("未绑定账户", true);
            status.Foreground = Brushes.DarkRed;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 15, 0, 8);
            body.Children.Add(status);
            figures = Label("", false);
            figures.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(figures);

            body.Children.Add(Btn("开始新交易日（需空仓且无活动订单）", StartDay));
            body.Children.Add(Btn("手动锁定（不平仓）", () => Lock("手动锁定")));
            body.Children.Add(Btn("第一次违规：30 分钟冷静期", () =>
            {
                lock (guard) pauseUntil = DateTime.Now.AddMinutes(30);
                Lock("违规冷静期；到期后仍需人工复核并解锁");
            }));
            body.Children.Add(Btn("模拟测试：复核后解锁", UnlockAfterReview));

            body.Children.Add(Label("单笔风险预检查（不执行下单）", true));
            instrumentInput = new ComboBox { Height = 28, Margin = new Thickness(0, 4, 0, 5) };
            instrumentInput.Items.Add("MNQ"); instrumentInput.Items.Add("MES"); instrumentInput.Items.Add("MGC");
            instrumentInput.SelectedIndex = 0;
            body.Children.Add(instrumentInput);
            body.Children.Add(Label("结构性止损距离（MNQ/MES 用指数点，MGC 用黄金价格 USD）：", false));
            stopInput = new TextBox { Text = "75", Height = 28 };
            body.Children.Add(stopInput);
            body.Children.Add(Label("手数：", false));
            qtyInput = new TextBox { Text = "1", Height = 28 };
            body.Children.Add(qtyInput);
            body.Children.Add(Btn("计算计划风险", ComputePreflight));
            preflight = Label("", false);
            preflight.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(preflight);
            body.Children.Add(Label("注意：单笔 $150、合计 $200、日亏 $300、3 笔、缓冲 $500 均是研究参数。\n"
                + "监控不计算手续费/滑点；不能从普通 Chart Trader 下单前确认你的结构止损。\n"
                + "锁定时仅在模拟账户中尝试取消新的开仓单；不能阻止瞬时成交、其他终端下单，且可能无法识别复杂 ATM/反手订单。", false));

            // Closing the window MUST NOT silently lift a lock.
            panel.Closed += (s, e) =>
            {
                if (timer != null) { timer.Stop(); timer = null; }
                Detach();
                panel = null;
            };

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => RefreshMetrics();
            timer.Start();
            RefreshAccountList();
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
            accounts.Items.Clear();
            foreach (string name in names) accounts.Items.Add(name);
            if (names.Count > 0)
                accounts.SelectedItem = names.Contains(previouslySelected) ? previouslySelected : names[0];
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
            // Fail-closed after process restart or reattachment: never auto-unlock.
            lock (guard)
            {
                locked = true;
                reason = "启动/切换账户后需人工核对状态并解锁";
            }
            SaveState();
            account.ExecutionUpdate += OnExecutionUpdate;
            account.OrderUpdate += OnOrderUpdate;
            account.AccountItemUpdate += OnAccountItemUpdate;
            mllInput.Text = Double.IsNaN(manualMll) ? "" : manualMll.ToString("F2", CultureInfo.InvariantCulture);
            RefreshMetrics();
        }

        private void Detach()
        {
            if (account != null)
            {
                account.ExecutionUpdate -= OnExecutionUpdate;
                account.OrderUpdate -= OnOrderUpdate;
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
                locked = true;
                tradingDate = DateTime.MinValue;
                realizedStart = 0;
                entries = 0;
                manualMll = double.NaN;
                seenEntryOrders.Clear();
                seenExecutions.Clear();
                cancellationRequested.Clear();
                pauseUntil = DateTime.MinValue;
                string path = StateFile();
                if (!File.Exists(path)) return;
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length < 5) return;
                    tradingDate = DateTime.Parse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    realizedStart = double.Parse(lines[1], CultureInfo.InvariantCulture);
                    entries = int.Parse(lines[2], CultureInfo.InvariantCulture);
                    double x;
                    if (double.TryParse(lines[3], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && x > 0)
                        manualMll = x;
                    pauseUntil = DateTime.Parse(lines[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                }
                catch (Exception) { locked = true; reason = "状态文件异常，必须人工检查"; }
            }
        }

        private void SaveState()
        {
            if (String.IsNullOrEmpty(currentAccountName)) return;
            try
            {
                string data;
                lock (guard)
                    data = tradingDate.ToString("o", CultureInfo.InvariantCulture) + "\n"
                        + realizedStart.ToString("R", CultureInfo.InvariantCulture) + "\n"
                        + entries.ToString(CultureInfo.InvariantCulture) + "\n"
                        + manualMll.ToString("R", CultureInfo.InvariantCulture) + "\n"
                        + pauseUntil.ToString("o", CultureInfo.InvariantCulture) + "\n"
                        + "LOCKED=TRUE; re-arm required after restart\n";
                File.WriteAllText(StateFile(), data, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Do not mistake an unwritable file for a working persistent lock.
                lock (guard) { locked = true; reason = "状态保存失败：" + ex.Message; }
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

        private void StartDay()
        {
            if (account == null) return;
            if (HasPosition(account) || HasActiveOrder(account))
            {
                MessageBox.Show("必须先空仓、确认无活动订单，再初始化新交易日。"); return;
            }
            if (MessageBox.Show("这将清零本插件的入场计数和日内盈亏基准。请先核对 NT8 成交记录及平台状态。继续？",
                    "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            lock (guard)
            {
                tradingDate = DateTime.Now.Date; // PC-local date, not broker/session boundary
                realizedStart = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                entries = 0;
                seenEntryOrders.Clear(); seenExecutions.Clear(); cancellationRequested.Clear();
                pauseUntil = DateTime.MinValue;
                locked = true; reason = "新交易日已初始化，仍需复核后手动解锁";
            }
            SaveState(); RefreshMetrics();
        }

        private void SaveMll()
        {
            if (account == null) return;
            double x;
            if (!double.TryParse(mllInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out x) || x <= 0)
            { MessageBox.Show("输入有效的 USD MLL，例如 48500。必须与选定模拟账户权益相匹配。"); return; }
            lock (guard) manualMll = x;
            SaveState(); RefreshMetrics();
        }

        private void Lock(string why)
        {
            lock (guard) { locked = true; reason = why; }
            SaveState();
            RefreshMetrics();
        }

        private void UnlockAfterReview()
        {
            if (account == null) return;
            if (tradingDate != DateTime.Now.Date)
            { MessageBox.Show("先执行“开始新交易日”并核对已有成交；本插件按电脑本地日期计日。"); return; }
            if (DateTime.Now < pauseUntil)
            { MessageBox.Show("冷静期尚未结束，不能解锁。"); return; }
            if (HasPosition(account) || HasActiveOrder(account))
            { MessageBox.Show("测试版本只允许在空仓且无活动订单时解锁。"); return; }
            if (Double.IsNaN(manualMll))
            { MessageBox.Show("请先输入并保存手动 MLL。"); return; }
            double realized = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            double nlv = account.Get(AccountItem.NetLiquidation, Currency.UsDollar);
            if (!Valid(nlv) || nlv - manualMll < BufferFloor || entries >= EntryCap || realized - realizedStart <= -DailyLossCap)
            { MessageBox.Show("未满足解锁条件：请核对权益/MLL/日亏或笔数。"); return; }
            if (MessageBox.Show("仅模拟账户：你已确认状态、MLL、持仓、挂单和交易记录？\n本插件不能阻止所有订单。",
                "复核解锁", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            lock (guard) { locked = false; reason = "已解锁（模拟测试）"; }
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
            if (changed) SaveState();
            if (panel != null) panel.Dispatcher.BeginInvoke(new Action(RefreshMetrics));
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            if (account == null || e.Order == null) return;
            bool isLocked;
            lock (guard) isLocked = locked;
            if (!isLocked) return;
            if (e.OrderState != OrderState.Submitted &&
                e.OrderState != OrderState.Accepted && e.OrderState != OrderState.Working) return;

            // Conservative: only cancel clearly opening orders while FLAT on this instrument.
            // Never cancel stop/target orders for an existing position.
            bool flat = true;
            lock (account.Positions)
                flat = !account.Positions.Any(p => p.Instrument == e.Order.Instrument &&
                    p.MarketPosition != MarketPosition.Flat && p.Quantity != 0);
            if (!flat) return;
            bool opening = e.Order.OrderAction == OrderAction.Buy ||
                           e.Order.OrderAction == OrderAction.SellShort ||
                           e.Order.OrderAction == OrderAction.Sell;
            if (!opening) return;
            lock (guard)
                if (!cancellationRequested.Add(e.Order)) return;
            try { account.Cancel(new[] { e.Order }); }
            catch (Exception ex)
            {
                lock (guard) reason = "撤单失败，立即人工检查：" + ex.Message;
            }
            if (panel != null) panel.Dispatcher.BeginInvoke(new Action(RefreshMetrics));
        }

        private void RefreshMetrics()
        {
            if (status == null || figures == null) return;
            if (account == null) { status.Text = "未连接模拟账户"; figures.Text = ""; return; }
            double r, u, nlv;
            try
            {
                r = account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                u = account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                nlv = account.Get(AccountItem.NetLiquidation, Currency.UsDollar);
            }
            catch (Exception ex)
            {
                Lock("无法读取账户数值：" + ex.Message); return;
            }
            double baseline; int count; bool isLocked; DateTime date, until; string why; double mll;
            lock (guard)
            { baseline = realizedStart; count = entries; isLocked = locked;
              date = tradingDate; until = pauseUntil; why = reason; mll = manualMll; }
            double sessionPnl = (r - baseline) + u;
            double buffer = nlv - mll;
            if (!isLocked && (date != DateTime.Now.Date ||
                sessionPnl <= -DailyLossCap || count >= EntryCap ||
                !Valid(nlv) || Double.IsNaN(mll) || buffer < BufferFloor))
            {
                Lock("触发日期/亏损/笔数/回撤缓冲安全条件"); return;
            }
            status.Text = (isLocked ? "🔒 已锁定" : "● 已解锁") + "  |  " + why;
            status.Foreground = isLocked ? Brushes.DarkRed : Brushes.DarkGreen;
            figures.Text = "账户：" + account.Name + "\n"
                + "当前模式：仅模拟账户；锁单尝试撤单，不保证成功\n"
                + "日内近似净盈亏：" + (date == DateTime.Now.Date ? sessionPnl.ToString("F2") : "未初始化") + " USD\n"
                + "成交开仓订单数：" + count + "/" + EntryCap + "（可能遗漏离线或复杂 ATM 成交）\n"
                + "净清算权益：" + nlv.ToString("F2") + " USD\n"
                + "手动 MLL：" + (Double.IsNaN(mll) ? "未设置" : mll.ToString("F2")) + " USD\n"
                + "剩余回撤估算：" + (Double.IsNaN(mll) ? "未知" : buffer.ToString("F2")) + " USD\n"
                + "冷静期结束：" + (until > DateTime.Now ? until.ToString("yyyy-MM-dd HH:mm:ss") : "无") + "\n"
                + "合计持仓风险（$" + MaxCombinedRisk + "）：未自动验证，需独立监控";
        }

        private void ComputePreflight()
        {
            double distance; int qty;
            if (!double.TryParse(stopInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out distance) ||
                !int.TryParse(qtyInput.Text, out qty) || distance <= 0 || qty <= 0)
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
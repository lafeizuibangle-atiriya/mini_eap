using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiniEapHost
{
    /// <summary>
    /// EAP 主控端主窗体：
    /// 上半区设备状态列表 + 命令按钮，下半区报警列表 + 消息日志。
    /// 报警与运行结束事件实时入库（MySQL 存储过程，MySQL 不可用时自动降级为本地文件）。
    /// </summary>
    public class MainForm : Form
    {
        private TcpDeviceClient _client;
        private IDbStore _store;
        private ListView _lvDevices;
        private ListView _lvAlarms;
        private ListBox _lbLog;
        private TextBox _tbHost;
        private TextBox _tbPort;
        private TextBox _tbDevice;
        private TextBox _tbRecipe;
        private Button _btnConnect;
        private Button _btnDisconnect;
        private StatusStrip _status;

        private readonly Dictionary<string, ListViewItem> _deviceRows = new();

        public MainForm()
        {
            Text = "Mini-EAP 设备自动化主控端";
            Size = new Size(920, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            _ = InitStoreAsync();
        }

        private async Task InitStoreAsync()
        {
            // 连接串放配置文件或环境变量均可；默认指向本地 mini_eap 库
            string connStr = Environment.GetEnvironmentVariable("MINIEAP_MYSQL")
                ?? "Server=localhost;Port=3306;Database=mini_eap;Uid=root;Pwd=123456;";
            _store = await StoreFactory.CreateAsync(connStr,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "minieap_events.jsonl"));
            BeginInvoke(() => SetStatus($"存储: {_store.Mode}" + (_store.Mode == "File" ? "（MySQL 未连接，已降级本地文件）" : "")));
        }

        private void BuildUi()
        {
            // ---- 顶部连接栏 ----
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
            top.Controls.Add(new Label { Text = "设备地址:", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            _tbHost = new TextBox { Text = "127.0.0.1", Width = 90 };
            _tbPort = new TextBox { Text = "9000", Width = 55 };
            _btnConnect = new Button { Text = "连接设备", Width = 80 };
            _btnDisconnect = new Button { Text = "断开", Width = 55, Enabled = false };
            _btnConnect.Click += (s, e) => DoConnect();
            _btnDisconnect.Click += (s, e) => { _client?.Disconnect(); };
            top.Controls.AddRange(new Control[] { _tbHost, _tbPort, _btnConnect, _btnDisconnect });

            // ---- 设备状态列表 ----
            _lvDevices = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            _lvDevices.Columns.Add("机台", 90);
            _lvDevices.Columns.Add("状态", 90);
            _lvDevices.Columns.Add("Recipe", 130);
            _lvDevices.Columns.Add("进度%", 60);
            _lvDevices.Columns.Add("腔温℃", 70);
            _lvDevices.Columns.Add("腔压", 60);

            // ---- 命令按钮区 ----
            var cmdPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(4) };
            _tbDevice = new TextBox { Text = "ETCH01", Width = 80 };
            _tbRecipe = new TextBox { Text = "ETCH_STD_01", Width = 120 };
            cmdPanel.Controls.Add(new Label { Text = "机台:", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            cmdPanel.Controls.Add(_tbDevice);
            cmdPanel.Controls.Add(new Label { Text = "Recipe:", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            cmdPanel.Controls.Add(_tbRecipe);
            foreach (var (text, cmd) in new[] {
                ("加载 Recipe", "LOAD_RECIPE"), ("START", "START"), ("PAUSE", "PAUSE"), ("STOP", "STOP"), ("复位报警", "RESET") })
            {
                var b = new Button { Text = text, Width = 92 };
                string captured = cmd;
                b.Click += (s, e) => SendCommand(captured);
                cmdPanel.Controls.Add(b);
            }
            var btnAck = new Button { Text = "确认选中报警", Width = 110 };
            btnAck.Click += (s, e) => ConfirmSelectedAlarm();
            cmdPanel.Controls.Add(btnAck);

            // ---- 报警列表 ----
            _lvAlarms = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            _lvAlarms.Columns.Add("时间", 130);
            _lvAlarms.Columns.Add("机台", 80);
            _lvAlarms.Columns.Add("代码", 70);
            _lvAlarms.Columns.Add("级别", 60);
            _lvAlarms.Columns.Add("内容", 200);
            _lvAlarms.Columns.Add("数值", 60);

            // ---- 日志 ----
            _lbLog = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

            var bottomSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 300
            };
            var alarmGroup = new GroupBox { Text = "报警", Dock = DockStyle.Fill };
            alarmGroup.Controls.Add(_lvAlarms);
            var logGroup = new GroupBox { Text = "消息日志", Dock = DockStyle.Fill };
            logGroup.Controls.Add(_lbLog);
            bottomSplit.Panel1.Controls.Add(alarmGroup);
            bottomSplit.Panel2.Controls.Add(logGroup);

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            var devGroup = new GroupBox { Text = "设备状态", Dock = DockStyle.Fill };
            devGroup.Controls.Add(_lvDevices);
            mainSplit.Panel1.Controls.Add(devGroup);
            mainSplit.Panel2.Controls.Add(bottomSplit);

            Controls.Add(mainSplit);
            Controls.Add(cmdPanel);
            Controls.Add(top);
            Controls.Add(_status = new StatusStrip());
            _status.Items.Add(new ToolStripStatusLabel { Text = "存储: 初始化中..." });
        }

        private void SetStatus(string text)
        {
            if (_status.Items.Count > 0) _status.Items[0].Text = text;
        }

        private void DoConnect()
        {
            try
            {
                _client = new TcpDeviceClient();
                _client.LineReceived += OnLineReceived;
                _client.Disconnected += reason => BeginInvoke(() => Log("连接: " + reason));
                _client.Connect(_tbHost.Text.Trim(), int.Parse(_tbPort.Text.Trim()));
                _btnConnect.Enabled = false;
                _btnDisconnect.Enabled = true;
                Log($"已连接 {_tbHost.Text}:{_tbPort.Text}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("连接失败: " + ex.Message);
            }
        }

        private void SendCommand(string cmd)
        {
            if (_client == null || !_client.IsConnected)
            {
                Log("未连接设备");
                return;
            }
            var payload = new { cmd, device = _tbDevice.Text.Trim(), recipe = _tbRecipe.Text.Trim() };
            _client.Send(JsonSerializer.Serialize(payload));
        }

        private void OnLineReceived(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                string type = doc.RootElement.GetProperty("type").GetString();
                switch (type)
                {
                    case "status":
                        var st = JsonSerializer.Deserialize<EquipmentStatus>(line);
                        BeginInvoke(() => UpdateDeviceRow(st));
                        break;
                    case "alarm":
                    case "alarm_clear":
                        var al = JsonSerializer.Deserialize<AlarmEvent>(line);
                        BeginInvoke(() => AddAlarmRow(al));
                        break;
                    case "run_done":
                        var rd = JsonSerializer.Deserialize<RunDoneEvent>(line);
                        BeginInvoke(() =>
                        {
                            Log($"[{rd.Device}] 运行结束 {rd.Recipe} -> {rd.Result}");
                            _ = _store.FinishRunAsync(rd);
                        });
                        break;
                    case "ack":
                        var ac = JsonSerializer.Deserialize<AckMessage>(line);
                        BeginInvoke(() => Log($"[{ac.Device}] {ac.Cmd}: {(ac.Ok ? "OK" : "失败")} - {ac.Msg}"));
                        break;
                }
            }
            catch (Exception ex)
            {
                BeginInvoke(() => Log("解析异常: " + ex.Message));
            }
        }

        private void UpdateDeviceRow(EquipmentStatus st)
        {
            if (!_deviceRows.TryGetValue(st.Device, out var row))
            {
                row = new ListViewItem(st.Device) { Tag = st.Device };
                row.SubItems.AddRange(new[] { "", "", "", "", "" });
                _lvDevices.Items.Add(row);
                _deviceRows[st.Device] = row;
            }
            row.SubItems[1].Text = st.State;
            row.SubItems[2].Text = st.Recipe;
            row.SubItems[3].Text = st.Progress.ToString();
            row.SubItems[4].Text = st.ChamberTemp.ToString("0.0");
            row.SubItems[5].Text = st.Pressure.ToString("0.00");
            row.BackColor = st.State switch
            {
                "ALARM" => Color.MistyRose,
                "PROCESSING" => Color.Honeydew,
                "PAUSED" => Color.LemonChiffon,
                _ => Color.White
            };
        }

        private void AddAlarmRow(AlarmEvent al)
        {
            string time = DateTimeOffset.FromUnixTimeSeconds(al.Ts).LocalDateTime.ToString("HH:mm:ss");
            var row = new ListViewItem(time);
            string text = al.Type == "alarm_clear" ? $"[{al.Code}] 报警复位" : $"[{al.Code}] {al.Text}";
            row.SubItems.AddRange(new[] { al.Device, al.Code, al.Type == "alarm_clear" ? "-" : al.Level, text, al.Value.ToString("0.0") });
            row.Tag = al;
            _lvAlarms.Items.Insert(0, row);
            // 报警入库：触发走 sp_add_alarm，复位走 sp_clear_alarm
            if (_store != null)
                _ = al.Type == "alarm_clear" ? _store.ClearAlarmAsync(al) : _store.AddAlarmAsync(al);
        }

        /// <summary>确认选中报警：向设备发 RESET 并记录日志（界面层演示报警确认闭环）。</summary>
        private void ConfirmSelectedAlarm()
        {
            if (_lvAlarms.SelectedItems.Count == 0) { Log("请先在报警列表中选中一条报警"); return; }
            if (_lvAlarms.SelectedItems[0].Tag is not AlarmEvent al) return;
            Log($"[{al.Device}] 确认报警 {al.Code}，发送 RESET");
            SendCommand("RESET");
        }

        private void Log(string text)
        {
            _lbLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
            if (_lbLog.Items.Count > 200) _lbLog.Items.RemoveAt(200);
        }
    }
}

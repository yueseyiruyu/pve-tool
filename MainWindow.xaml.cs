using Renci.SshNet; // 必须引用 SSH.NET
using System;
using System.IO; // 添加用于路径处理
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media; // 用于颜色设置
using Microsoft.Win32;
using Microsoft.VisualBasic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace PVETool
{
    public partial class MainWindow : Window
    {
        // 【核心】定义全局 SSH 客户端对象（添加 ? 解决 CS8618 警告）
        private SshClient? _sshClient;
        // 标记当前是否处于内部更新状态避免循环
        private bool _isInternalUpdating = false;
        // --- 用于重连的私有凭据记录 ---
        private string _lastHost = "";
        private string _lastUser = "";
        private string _lastPass = "";
        // 本地或远程选择的镜像路径（本地为客户端路径，远程为服务器路径）
        private string _selectedImageLocalPath = "";
        private string _selectedImageRemotePath = "/mnt/game-disk/master/game-disk.qcow2";
        // 如果用户通过下拉选择了下载链接，缓存该链接以便在应用时使用
        private string _selectedImageRemoteUrl = "";
        // 默认远程镜像目录（通过 SSH 检测）
        private string? _defaultRemoteImageDir = null;
        // 当前在 PVE 默认目录下选中的镜像文件名（用于挂载）
        private string? _selectedRemoteImageName = null;
        // 是否已提示过 sensors 不存在（只提示一次）
        private bool _sensorsWarned = false;
        // 实时指标相关
        private System.Windows.Threading.DispatcherTimer? _metricsTimer;
        private long _prevTx = 0;
        private long _prevRx = 0;
        private DateTime _prevNetTime = DateTime.MinValue;
        private string _netIface = "";
        // CPU jiffies 上次值
        private long _prevTotalJiffies = 0;
        private long _prevIdleJiffies = 0;

        public MainWindow()
        {
            InitializeComponent();

            // 绑定按钮点击事件
            ConnectBtn.Click += ConnectBtn_Click;
            // 绑定初始化按钮事件
            InitBtn.Click += InitBtn_Click;

            // --- 添加：绑定安装驱动按钮 ---
            if (FindName("InstallDriverBtn") is System.Windows.Controls.Button btn)
                btn.Click += InstallDriverBtn_Click;
            // --- 添加：绑定安装显卡补丁按钮 ---
            if (FindName("InstallPatchBtn") is System.Windows.Controls.Button patchBtn)
                patchBtn.Click += InstallPatchBtn_Click;
            // 绑定“创建母机”按钮 (x:Name 为 cjmuji)
            if (FindName("cjmuji") is System.Windows.Controls.Button cjBtn)
                cjBtn.Click += cjmuji_Click;
            // 镜像选择：当选择“上传”时立即弹出文件选择器
            if (FindName("ImageSelectCombo") is ComboBox imgCombo)
                imgCombo.SelectionChanged += ImageSelectCombo_SelectionChanged;
            //修改所有配置
            if (FindName("updatehw") is Button updatehwBtn)
                updatehwBtn.Click += updatehw_Click;
            //克隆
            if (FindName("StartBatchBtn") is Button batchBtn) 
                batchBtn.Click += StartBatchBtn_Click;
            // 注册“母鸡转模板”按钮 (cjtemplate)
            if (FindName("cjtemplate") is System.Windows.Controls.Button templateBtn)
                templateBtn.Click += cjtemplate_Click;
            // 启动母机按钮 (x:Name="startmuji")
            if (FindName("startmuji") is System.Windows.Controls.Button startBtn)
                startBtn.Click += startmuji_Click;
            //删除所有虚拟机按钮
            if (FindName("delall") is System.Windows.Controls.Button delBtn)
                delBtn.Click += delall_Click;
            //防爆盘
            if (FindName("fangbao") is System.Windows.Controls.Button fangBtn)
                fangBtn.Click += fangbao_Click;
            //清理缓存
            if (FindName("clear_cache") is System.Windows.Controls.Button clearBtn)
                clearBtn.Click += clear_cache_Click;
            //同步D盘
            if (FindName("tbvm") is Button tbvmBtn)
                tbvmBtn.Click += tbvm_Click;
            //批量开机
            if (FindName("BtnBatchStart") is Button BtnBatchStart)
                BtnBatchStart.Click += BtnBatchStart_Click;
            //批量关机
            if (FindName("BtnBatchStop") is Button BtnBatchStop)
                BtnBatchStop.Click += BtnBatchStop_Click;
            //批量重启
            if (FindName("BtnBatchReboot") is Button BtnBatchReboot)
                BtnBatchReboot.Click += BtnBatchReboot_Click;
            //删除指定
            if (FindName("BtnBatchDelete") is Button BtnBatchDelete)
                BtnBatchDelete.Click += BtnBatchDelete_Click;
            //应用推荐配置
            if (FindName("BtnApplyPreset") is Button BtnApplyPreset)
                BtnApplyPreset.Click += BtnApplyPreset_Click;
            //推荐配置的输入框
            // 初始化实时指标定时器（每 3 秒轮询一次）
            _metricsTimer = new System.Windows.Threading.DispatcherTimer();
            _metricsTimer.Interval = TimeSpan.FromSeconds(3);
            _metricsTimer.Tick += MetricsTimer_Tick;
            _metricsTimer.Start();

        }
        //状态监视
        private async Task PrimeCpuJiffiesAsync()
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;
            try
            {
                var stat = await Task.Run(() => _sshClient.RunCommand("grep '^cpu ' /proc/stat || cat /proc/stat | grep '^cpu '"));
                var s = (stat.Result ?? "").Trim();
                if (!string.IsNullOrEmpty(s))
                {
                    var parts = System.Text.RegularExpressions.Regex.Split(s, "\\s+");
                    if (parts.Length >= 5)
                    {
                        long user = long.Parse(parts[1]);
                        long nice = long.Parse(parts[2]);
                        long system = long.Parse(parts[3]);
                        long idle = long.Parse(parts[4]);
                        long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
                        long irq = parts.Length > 6 ? long.Parse(parts[6]) : 0;
                        long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;
                        long steal = parts.Length > 8 ? long.Parse(parts[8]) : 0;
                        long total = user + nice + system + idle + iowait + irq + softirq + steal;
                        long idleAll = idle + iowait;
                        _prevTotalJiffies = total;
                        _prevIdleJiffies = idleAll;
                    }
                }
            }
            catch { }
        }

        private class ApiMetricsResult
        {
            public bool Success { get; set; }
            public int CpuPercent { get; set; }
            public int MemUsed { get; set; }
            public int MemTotal { get; set; }
            public int DiskPercent { get; set; }
            public string UptimeText { get; set; } = "-";
            public string CpuTemp { get; set; } = "-";
            public string GpuTemp { get; set; } = "-";
            public string? GpuMemText { get; set; }
            public double NetUp { get; set; }
            public double NetDown { get; set; }
            public bool ProvidesGpuInfo { get; set; }
        }

        // 尝试使用 Proxmox API 获取节点/母机的指标。需节点允许并且用户已配置凭据。
        private async Task<ApiMetricsResult> TryFetchMetricsViaApiAsync()
        {
            var res = new ApiMetricsResult { Success = false };
            try
            {
                // 只有当已连接并且我们拥有主机信息时尝试使用 API
                if (_sshClient == null || !_sshClient.IsConnected) return res;

                // 构造基础 URL（假设使用 https://{host}:8006/api2/json）
                string host = _sshClient.ConnectionInfo.Host;
                string user = _sshClient.ConnectionInfo.Username;
                // 注意：这里无法直接获得密码凭证的安全 token，且 Proxmox API 需要 ticket / token 登录。
                // 因此本实现尝试使用 unauthenticated endpoints 作为回退（大多数环境下不可用）。
                // 若你希望真正通过 Proxmox API 获取，需要提前实现 API 登录并缓存 ticket/csrf token。

                string baseUrl = $"https://{host}:8006/api2/json";
                using (var handler = new HttpClientHandler())
                {
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        // 尝试获取节点状态（/nodes/{node}/status)
                        // 先获取节点名称
                        var hn = _sshClient.RunCommand("hostname");
                        string node = hn.Result.Trim();
                        if (string.IsNullOrEmpty(node)) return res;

                        var apiUrl = $"{baseUrl}/nodes/{node}/status";
                        var r = await client.GetAsync(apiUrl);
                        if (!r.IsSuccessStatusCode) return res;
                        var txt = await r.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(txt))
                        {
                            var root = doc.RootElement.GetProperty("data");
                            // cpu
                            if (root.TryGetProperty("cpu", out var cpuElem))
                            {
                                double cpu = cpuElem.GetDouble();
                                res.CpuPercent = (int)Math.Round(cpu * 100);
                            }
                            // memory
                            if (root.TryGetProperty("memory", out var memElem) && root.TryGetProperty("maxmem", out var maxElem))
                            {
                                int used = memElem.GetInt32();
                                int total = maxElem.GetInt32();
                                res.MemUsed = used / 1024 / 1024; // bytes -> MB
                                res.MemTotal = total / 1024 / 1024;
                            }
                            // uptime
                            if (root.TryGetProperty("uptime", out var upElem))
                            {
                                int secs = upElem.GetInt32();
                                TimeSpan ts = TimeSpan.FromSeconds(secs);
                                res.UptimeText = $"在线: {ts.Days}d {ts.Hours}h";
                            }
                            // disk: try disk usage root
                            // net: not provided here easily
                        }
                        res.Success = true;
                    }
                }
            }
            catch { }
            return res;
        }

        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 B/s";
            string[] suf = { "B/s", "KB/s", "MB/s", "GB/s" };
            int idx = 0;
            double val = bytesPerSec;
            while (val >= 1024 && idx < suf.Length - 1)
            {
                val /= 1024.0;
                idx++;
            }
            return $"{val:F1} {suf[idx]}";
        }
        // This is a new line added for demonstration purposes.

        // 定时器回调
        private async void MetricsTimer_Tick(object? sender, EventArgs e)
        {
            try { await FetchAndUpdateMetricsAsync(); } catch { }
        }

        // 异步获取并更新远程/本地指标
        private async Task FetchAndUpdateMetricsAsync()
        {
            // 首先尝试通过 Proxmox API 获取（优先使用 API，尽量不执行远程命令）
            var apiMetrics = await TryFetchMetricsViaApiAsync();
            if (apiMetrics.Success)
            {
                // 使用 API 数据更新 UI
                Dispatcher.Invoke(() =>
                {
                    CpuProgress.Value = apiMetrics.CpuPercent;
                    CpuPercentText.Text = apiMetrics.CpuPercent + "%";
                    if (apiMetrics.MemTotal > 0)
                    {
                        MemProgress.Value = apiMetrics.MemUsed * 100 / apiMetrics.MemTotal;
                        MemPercentText.Text = (apiMetrics.MemUsed * 100 / apiMetrics.MemTotal) + "%";
                    }
                    else
                    {
                        MemProgress.Value = 0; MemPercentText.Text = "-%";
                    }
                    DiskProgress.Value = apiMetrics.DiskPercent;
                    DiskPercentText.Text = apiMetrics.DiskPercent + "%";
                    UptimeText.Text = apiMetrics.UptimeText;
                    // 如果 API 提供 GPU 信息，则显示，否则保持 -，并后续尝试通过 SSH 获取
                    TempText.Text = $"CPU: {apiMetrics.CpuTemp}  /  显卡: {apiMetrics.GpuTemp}";
                    GpuMemText.Text = apiMetrics.GpuMemText ?? "-";
                    NetUpText.Text = FormatSpeed(apiMetrics.NetUp);
                    NetDownText.Text = FormatSpeed(apiMetrics.NetDown);
                });

                // 如果 API 返回了完整关键指标（CPU/Mem/Disk/Temp/GPU），就不再回退到 SSH
                bool apiHasCore = apiMetrics.CpuPercent > 0 && apiMetrics.MemTotal > 0 && apiMetrics.DiskPercent > 0;
                bool apiHasTemps = apiMetrics.CpuTemp != "-" && (apiMetrics.ProvidesGpuInfo || apiMetrics.GpuTemp != "-");
                if ((apiHasCore && apiHasTemps) || _sshClient == null || !_sshClient.IsConnected) return;
            }

            if (_sshClient != null && _sshClient.IsConnected)
            {
                try
                {
                    var data = await Task.Run(() =>
                    {
                        var stat1 = _sshClient.RunCommand("grep '^cpu ' /proc/stat || cat /proc/stat | grep '^cpu '");
                        var memCmd = _sshClient.RunCommand("free -m | awk 'NR==2{printf \"%d %d\", $3, $2}'");
                        var diskCmd = _sshClient.RunCommand("df --output=pcent / | tail -n1 || df -h / | awk 'NR==2{print $5}'");
                        var uptimeCmd = _sshClient.RunCommand("cat /proc/uptime | awk '{print int($1)}'");
                        var cpuTempCmd = _sshClient.RunCommand("(sensors 2>/dev/null | grep -E 'Package id 0|Core 0' | head -n1) || echo ''");
                        var thermalCmd = _sshClient.RunCommand("for f in /sys/class/thermal/thermal_zone*/temp; do if [ -f \"$f\" ]; then cat $f; break; fi; done 2>/dev/null || echo ''");

                        // 检查 nvidia-smi 是否存在，只有存在时才执行以避免命令报错
                        var hasNvidiaCmd = _sshClient.RunCommand("command -v nvidia-smi >/dev/null 2>&1 && echo ok || echo no");
                        string nvidiaOut = "";
                        if ((hasNvidiaCmd.Result ?? "").Trim() == "ok")
                        {
                            var nvidiaCmd = _sshClient.RunCommand("nvidia-smi --query-gpu=temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits 2>/dev/null || echo ''");
                            nvidiaOut = nvidiaCmd.Result ?? "";
                        }

                        // 网络统计（获取默认路由接口并读取 tx/rx 字节计数）
                        string netIface = "";
                        try
                        {
                            var nif = _sshClient.RunCommand("ip -o -4 route show to default | awk '{print $5}'");
                            netIface = (nif.Result ?? "").Trim();
                        }
                        catch { netIface = _netIface ?? ""; }

                        long tx = 0, rx = 0;
                        if (!string.IsNullOrEmpty(netIface))
                        {
                            try
                            {
                                var txc = _sshClient.RunCommand($"cat /sys/class/net/{netIface}/statistics/tx_bytes 2>/dev/null || echo 0");
                                var rxc = _sshClient.RunCommand($"cat /sys/class/net/{netIface}/statistics/rx_bytes 2>/dev/null || echo 0");
                                long.TryParse((txc.Result ?? "0").Trim(), out tx);
                                long.TryParse((rxc.Result ?? "0").Trim(), out rx);
                            }
                            catch { tx = 0; rx = 0; }
                        }

                        // 处理 CPU 使用率：如果这是第一次采样，做第二次短间隔采样以立即得到一个值
                        int cpuPercentLocal = 0;
                        try
                        {
                            var s1 = (stat1.Result ?? "").Trim();
                            var parts1 = System.Text.RegularExpressions.Regex.Split(s1, "\\s+");
                            long total1 = 0, idle1 = 0;
                            if (parts1.Length >= 5)
                            {
                                long user = long.Parse(parts1[1]);
                                long nice = long.Parse(parts1[2]);
                                long system = long.Parse(parts1[3]);
                                long idle = long.Parse(parts1[4]);
                                long iowait = parts1.Length > 5 ? long.Parse(parts1[5]) : 0;
                                long irq = parts1.Length > 6 ? long.Parse(parts1[6]) : 0;
                                long softirq = parts1.Length > 7 ? long.Parse(parts1[7]) : 0;
                                long steal = parts1.Length > 8 ? long.Parse(parts1[8]) : 0;
                                total1 = user + nice + system + idle + iowait + irq + softirq + steal;
                                idle1 = idle + iowait;
                            }

                            long total2 = total1, idle2 = idle1;
                            // 如果 we don't have previous totals, do a quick second sample
                            if (_prevTotalJiffies == 0)
                            {
                                System.Threading.Thread.Sleep(500);
                                var stat2 = _sshClient.RunCommand("grep '^cpu ' /proc/stat || cat /proc/stat | grep '^cpu '");
                                var s2 = (stat2.Result ?? "").Trim();
                                var parts2 = System.Text.RegularExpressions.Regex.Split(s2, "\\s+");
                                if (parts2.Length >= 5)
                                {
                                    long user2 = long.Parse(parts2[1]);
                                    long nice2 = long.Parse(parts2[2]);
                                    long system2 = long.Parse(parts2[3]);
                                    long idle_2 = long.Parse(parts2[4]);
                                    long iowait2 = parts2.Length > 5 ? long.Parse(parts2[5]) : 0;
                                    long irq2 = parts2.Length > 6 ? long.Parse(parts2[6]) : 0;
                                    long softirq2 = parts2.Length > 7 ? long.Parse(parts2[7]) : 0;
                                    long steal2 = parts2.Length > 8 ? long.Parse(parts2[8]) : 0;
                                    total2 = user2 + nice2 + system2 + idle_2 + iowait2 + irq2 + softirq2 + steal2;
                                    idle2 = idle_2 + iowait2;
                                }
                            }
                            long totald = total2 - _prevTotalJiffies;
                            long idled = idle2 - _prevIdleJiffies;
                            if (totald <= 0)
                            {
                                // fallback to instantaneous if prev not reliable
                                totald = total2 - total1;
                                idled = idle2 - idle1;
                            }
                            if (totald > 0)
                            {
                                double usage = (1.0 - ((double)idled / totald)) * 100.0;
                                cpuPercentLocal = (int)Math.Round(usage);
                            }

                            // update prev jiffies
                            _prevTotalJiffies = total2;
                            _prevIdleJiffies = idle2;
                        }
                        catch { cpuPercentLocal = 0; }

                        return new
                        {
                            CpuPercent = cpuPercentLocal,
                            ProcStatOut = stat1.Result ?? "",
                            MemOut = memCmd.Result ?? "",
                            DiskOut = diskCmd.Result ?? "",
                            UptimeOut = uptimeCmd.Result ?? "",
                            CpuTempOut = cpuTempCmd.Result ?? "",
                            ThermalOut = thermalCmd.Result ?? "",
                            NvidiaOut = nvidiaOut,
                            NetIface = netIface,
                            NetTx = tx,
                            NetRx = rx
                        };
                    });

                    // Prefer CpuPercent returned by the quick Task sample (if available)
                    int cpuPercent = (data.CpuPercent > 0) ? data.CpuPercent : 0;
                    try
                    {
                        // 解析 /proc/stat 输出，格式: cpu  user nice system idle iowait irq softirq steal guest guest_nice
                        var statLine = (data.ProcStatOut ?? "").Trim();
                        if (!string.IsNullOrEmpty(statLine))
                        {
                            var parts = System.Text.RegularExpressions.Regex.Split(statLine, "\\s+");
                            // parts[0] == "cpu"
                            if (parts.Length >= 5)
                            {
                                long user = long.Parse(parts[1]);
                                long nice = long.Parse(parts[2]);
                                long system = long.Parse(parts[3]);
                                long idle = long.Parse(parts[4]);
                                long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
                                long irq = parts.Length > 6 ? long.Parse(parts[6]) : 0;
                                long softirq = parts.Length > 7 ? long.Parse(parts[7]) : 0;
                                long steal = parts.Length > 8 ? long.Parse(parts[8]) : 0;
                                long total = user + nice + system + idle + iowait + irq + softirq + steal;
                                long idleAll = idle + iowait;

                                // 计算差值
                                long prevTotal = _prevTotalJiffies;
                                long prevIdle = _prevIdleJiffies;
                                if (prevTotal > 0)
                                {
                                    long totald = total - prevTotal;
                                    long idled = idleAll - prevIdle;
                                    if (totald > 0)
                                    {
                                        double usage = (1.0 - ((double)idled / totald)) * 100.0;
                                        cpuPercent = (int)Math.Round(usage);
                                    }
                                }

                                _prevTotalJiffies = total;
                                _prevIdleJiffies = idleAll;
                            }
                        }
                    }
                    catch { cpuPercent = 0; }

                    // 诊断输出：若 sensors 无输出，仅提示一次
                    try
                    {
                        if (string.IsNullOrWhiteSpace(data.CpuTempOut) && !_sensorsWarned)
                        {
                            AppendLog("ℹ️ 传感器输出为空：服务器可能未安装 lm-sensors，或 sensors 命令不可用。");
                            _sensorsWarned = true;
                        }
                    }
                    catch { }

                    int memUsed = 0, memTotal = 0;
                    try
                    {
                        var ms = (data.MemOut ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (ms.Length >= 2)
                        {
                            memUsed = int.Parse(ms[0]);
                            memTotal = int.Parse(ms[1]);
                        }
                    }
                    catch { }

                    int diskPercent = 0;
                    try
                    {
                        var d = (data.DiskOut ?? "").Trim();
                        var m = System.Text.RegularExpressions.Regex.Match(d, "(\\d+)");
                        if (m.Success) diskPercent = int.Parse(m.Groups[1].Value);
                    }
                    catch { }

                    string uptimeText = "未知";
                    try
                    {
                        if (int.TryParse(data.UptimeOut.Trim(), out int secs))
                        {
                            TimeSpan ts = TimeSpan.FromSeconds(secs);
                            uptimeText = $"在线: {ts.Days}d {ts.Hours}h";
                        }
                    }
                    catch { }

                    string cpuTemp = "-";
                    try
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(data.CpuTempOut ?? "", "\\+?([0-9]+\\.?[0-9]*)\\s*°C");
                        if (m.Success) cpuTemp = m.Groups[1].Value + " °C";
                        else
                        {
                            // 尝试 thermal 输出（毫度或度）
                            try
                            {
                                var to = (data.ThermalOut ?? "").Trim();
                                if (long.TryParse(to, out long tv))
                                {
                                    if (tv > 1000) cpuTemp = (tv / 1000.0).ToString("F1") + " °C";
                                    else cpuTemp = tv + " °C";
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    string gpuTemp = "-";
                    string gpuMemText = "-";
                    try
                    {
                        var n = (data.NvidiaOut ?? "").Trim();
                        if (!string.IsNullOrEmpty(n))
                        {
                            var line = n.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                            var cols = line.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                            if (cols.Length >= 3)
                            {
                                gpuTemp = cols[0] + " °C";
                                if (int.TryParse(cols[1], out int used) && int.TryParse(cols[2], out int total))
                                {
                                    double usedG = Math.Round(used / 1024.0, 1);
                                    double totalG = Math.Round(total / 1024.0, 1);
                                    gpuMemText = $"{usedG} / {totalG} G";
                                }
                            }
                        }
                    }
                    catch { }

                    // 网络速度计算（基于 tx/rx 字节差值）
                    double upRate = 0, downRate = 0;
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (!string.IsNullOrEmpty(data.NetIface)) _netIface = data.NetIface;

                        if (_prevNetTime == DateTime.MinValue)
                        {
                            _prevNetTime = now;
                            _prevTx = data.NetTx;
                            _prevRx = data.NetRx;
                            upRate = 0; downRate = 0;
                        }
                        else
                        {
                            var span = (now - _prevNetTime).TotalSeconds;
                            if (span <= 0) span = 1;
                            var dtx = data.NetTx - _prevTx;
                            var drx = data.NetRx - _prevRx;
                            if (dtx < 0) dtx = 0;
                            if (drx < 0) drx = 0;
                            upRate = dtx / span;
                            downRate = drx / span;
                            _prevNetTime = now;
                            _prevTx = data.NetTx;
                            _prevRx = data.NetRx;
                        }
                    }
                    catch { upRate = 0; downRate = 0; }

                    Dispatcher.Invoke(() =>
                    {
                        CpuProgress.Value = Math.Min(100, Math.Max(0, cpuPercent));
                        MemProgress.Value = (memTotal > 0) ? (memUsed * 100 / memTotal) : 0;
                        DiskProgress.Value = Math.Min(100, Math.Max(0, diskPercent));
                        UptimeText.Text = uptimeText;
                        TempText.Text = $"CPU: {cpuTemp}  /  显卡: {gpuTemp}";
                        GpuMemText.Text = gpuMemText;
                        NetUpText.Text = FormatSpeed(upRate);
                        NetDownText.Text = FormatSpeed(downRate);
                        CpuPercentText.Text = $"{Math.Min(100, Math.Max(0, cpuPercent))}%";
                        MemPercentText.Text = (memTotal > 0) ? $"{(memUsed * 100 / memTotal)}%" : "-%";
                        DiskPercentText.Text = Math.Min(100, Math.Max(0, diskPercent)) + "%";
                    });
                }
                catch (Exception)
                {
                    // 忽略实时指标采集异常，日志已禁用以减少噪音
                }
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    UptimeText.Text = "离线";
                    TempText.Text = "CPU: -  /  显卡: -";
                    GpuMemText.Text = "-";
                });
            }
        }
        private string GetRemoteUptime()
        {
            if (_sshClient == null || !_sshClient.IsConnected) return "离线";
            var r = _sshClient.RunCommand("cat /proc/uptime | awk '{print int($1)}'");
            if (!int.TryParse(r.Result.Trim(), out int secs)) return "未知";
            TimeSpan ts = TimeSpan.FromSeconds(secs);
            return $"在线: {ts.Days}d {ts.Hours}h";
        }

        private string GetRemoteTemp()
        {
            if (_sshClient == null || !_sshClient.IsConnected) return "-";
            var r = _sshClient.RunCommand("(sensors 2>/dev/null | grep -E 'Package id 0|Core 0' | head -n1) || echo ''");
            var outS = r.Result.Trim();
            if (string.IsNullOrEmpty(outS)) return "-";
            var m = System.Text.RegularExpressions.Regex.Match(outS, "\\+?([0-9]+\\.?[0-9]*)\\s*°C");
            if (m.Success) return m.Groups[1].Value + " °C";
            return outS;
        }

        // 应用推荐配置并挂载镜像到母机
      
        /// <summary>
        /// 连接/断开 PVE 按钮主逻辑
        /// </summary>
        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            // --- 新增：断开连接逻辑 ---
            if (_sshClient != null && _sshClient.IsConnected)
            {
                try
                {
                    _sshClient.Disconnect();
                    _sshClient.Dispose();
                    _sshClient = null;

                    AppendLog("🔌 已主动断开与 PVE 的 SSH 连接。");
                    ConnectBtn.Content = "连接 PVE";
                    ConnectBtn.Background = new SolidColorBrush(Color.FromRgb(63, 81, 181)); // 恢复初始蓝色
                }
                catch (Exception ex)
                {
                    AppendLog($"❌ 断开连接时出现异常: {ex.Message}");
                }
                return; // 执行完断开即返回，不再往下走连接逻辑
            }

            // --- 原有：连接逻辑 ---
            string host = HostIp.Text.Trim();
            string user = UserName.Text.Trim();
            string pass = UserPass.Password;

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pass))
            {
                AppendLog("❌ 错误: IP 地址或密码不能为空！");
                return;
            }

            // 记录凭据用于后续重启后的自动连接
            _lastHost = host;
            _lastUser = user;
            _lastPass = pass;

            ConnectBtn.IsEnabled = false;
            ConnectBtn.Content = "连接中...";
            AppendLog($"--- 开始连接 PVE: {host} ---");

            try
            {
                await Task.Run(() =>
                {
                    _sshClient = new SshClient(host, user, pass);
                    _sshClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                    _sshClient.Connect();
                });

                if (_sshClient != null && _sshClient.IsConnected)
                {
                    AppendLog("✅ [SUCCESS] SSH 连接建立成功！");
                    _ = RefreshRemoteImagesAsync();
                    AppendLog($"[SYSTEM] 远程主机版本: {_sshClient.ConnectionInfo.ServerVersion}");
                    var testCmd = _sshClient.RunCommand("hostname");
                    AppendLog($"[SYSTEM] 节点名称: {testCmd.Result.Trim()}");
                    ConnectBtn.Content = "已连接 (点击断开)"; // 提示用户可以点击断开
                    ConnectBtn.Background = Brushes.Green;
                    // 连接成功后先读取一次 /proc/stat 以初始化 jiffies，避免 CPU% 首次为 0
                    try { await PrimeCpuJiffiesAsync(); } catch { }
                    // 连接成功后立即刷新一次实时指标，确保 UI 立刻更新
                    try { await FetchAndUpdateMetricsAsync(); } catch { }
                }
            }
            // 处理密码错误或用户名错误
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                AppendLog("❌ [AUTH ERROR] 身份验证失败：用户名或密码错误。");
                ConnectBtn.Content = "密码错误";
                ConnectBtn.Background = Brushes.Red;
            }
            // 处理网络不通或 IP 错误
            catch (System.Net.Sockets.SocketException)
            {
                AppendLog("❌ [NET ERROR] 无法连接：请检查 IP 地址或 22 端口是否开放。");
                ConnectBtn.Content = "超时/连不上";
                ConnectBtn.Background = Brushes.Red;
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [EXCEPTION] 连接发生异常: {ex.Message}");
                ConnectBtn.Content = "连接失败";
            }
            finally
            {
                ConnectBtn.IsEnabled = true;
            }
        }

        /// <summary>
        /// 初始化功能主逻辑：上传并解压 uabba.zip
        /// </summary>
        private async void InitBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1. 检查连接
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: 请先成功连接 PVE 服务器！");
                return;
            }

            // 2. 路径准备
            string fileName = "uabba.zip";
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", fileName);
            string remotePath = "/root/" + fileName;

            if (!File.Exists(localPath))
            {
                AppendLog($"❌ 错误: 未找到本地文件: {localPath}");
                return;
            }

            // 3. UI 状态
            InitBtn.IsEnabled = false;
            AppendLog("--- 开始执行初始化任务 ---");

            try
            {
                string host = _sshClient.ConnectionInfo.Host;
                string user = _sshClient.ConnectionInfo.Username;
                string pass = UserPass.Password;

                await Task.Run(() =>
                {
                    // A. 上传文件 (使用 SFTP)
                    AppendLog($"[1/3] 正在上传 {fileName}...");
                    using (var sftp = new SftpClient(host, user, pass))
                    {
                        sftp.Connect();
                        using (var fs = File.OpenRead(localPath))
                        {
                            sftp.UploadFile(fs, remotePath);
                        }
                        sftp.Disconnect();
                    }
                    AppendLog("✅ 上传完成。");

                    // B. 解压文件 (使用 Python3 原生解压)
                    AppendLog("[2/3] 正在使用系统自带工具解压到 /root/ ...");
                    string pyUnzipCmd = $"python3 -c \"import zipfile; z = zipfile.ZipFile('{remotePath}'); z.extractall('/root/'); z.close()\"";
                    var unzipCmd = _sshClient.RunCommand(pyUnzipCmd);

                    if (unzipCmd.ExitStatus == 0)
                        AppendLog("✅ 解压成功。");
                    else
                        AppendLog($"⚠️ 解压反馈: {unzipCmd.Error}");

                    // C. 清理
                    AppendLog("[3/3] 正在清理临时文件...");
                    _sshClient.RunCommand($"rm -f {remotePath}");
                });

                AppendLog("🎉 [SUCCESS] PVE 初始化全部完成！");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [EXCEPTION] 初始化失败: {ex.Message}");
            }
            finally
            {
                InitBtn.IsEnabled = true;
            }
        }

        // --- 严格核对整合版：安装驱动 + 健壮重连逻辑 ---

        private async void InstallDriverBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: 请先连接服务器。");
                return;
            }

            AppendLog("🚀 正在启动驱动安装程序...");
            bool isScriptFinished = false;

            try
            {
                await Task.Run(async () =>
                {
                    // 1. 安装时赋予权限
                    _sshClient.RunCommand("chmod +x /root/step1");

                    // 2. 启动脚本并获取 PID
                    var startCmd = _sshClient.CreateCommand("nohup /root/step1 > /root/install.log 2>&1 & echo $!");
                    string pid = startCmd.Execute().Trim();

                    if (string.IsNullOrEmpty(pid))
                    {
                        AppendLog("❌ 错误: 无法获取安装进程 PID。");
                        return;
                    }

                    // 优化打印：只在开始时提示一次
                    AppendLog("⏳ 正在安装 (脚本执行完毕前不会重复提醒)...");

                    // 3. 循环监测 PID 是否存在
                    bool isProcessRunning = true;
                    while (isProcessRunning)
                    {
                        await Task.Delay(3000);

                        // 检测 PID 文件夹是否存在
                        var checkCmd = _sshClient.RunCommand($"if [ -d /proc/{pid} ]; then echo \"running\"; fi");

                        if (checkCmd.Result.Trim() != "running")
                        {
                            isProcessRunning = false;
                            isScriptFinished = true;
                        }

                        // 如果 SSH 已经断开（说明服务器开始重启了）
                        if (!_sshClient.IsConnected)
                        {
                            isProcessRunning = false;
                            isScriptFinished = true;
                        }
                    }
                });
            }
            catch { isScriptFinished = true; }

            if (isScriptFinished)
            {
                AppendLog("✅ 检测到 Shell 脚本执行完毕！安装成功。");
                await StartAutoReconnect();
            }
        }

        private async Task StartAutoReconnect()
        {
            // 彻底释放旧的、已失效的全局连接对象
            if (_sshClient != null)
            {
                try { _sshClient.Dispose(); } catch { }
                _sshClient = null;
            }

            Dispatcher.Invoke(() => {
                ConnectBtn.Content = "等待重启...";
                ConnectBtn.Background = Brushes.Orange;
            });

            AppendLog("⌛ 正在检测系统重启状态 (遇到连接拒绝是正常现象，请稍候)...");

            while (true)
            {
                // 等待 5 秒，避免高频请求导致 Socket 资源耗尽
                await Task.Delay(5000);

                if (await TryConnectSilent())
                {
                    AppendLog("🎊 服务器已重启完成，连接已恢复！");
                    AppendLog("👉 下一步");
                    break;
                }
            }
        }

        private async Task<bool> TryConnectSilent()
        {
            // 如果记录的凭据丢失，直接返回失败
            if (string.IsNullOrEmpty(_lastHost) || string.IsNullOrEmpty(_lastPass)) return false;

            // 每次探测都使用独立的临时变量，确保不干扰全局对象
            SshClient? tempClient = null;
            try
            {
                bool connected = await Task.Run(() =>
                {
                    try
                    {
                        tempClient = new SshClient(_lastHost, _lastUser, _lastPass);
                        tempClient.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                        tempClient.Connect();
                        return tempClient.IsConnected;
                    }
                    catch
                    {
                        // 在 Task 内部吞掉具体连接异常，防止向上传播
                        if (tempClient != null) { tempClient.Dispose(); tempClient = null; }
                        return false;
                    }
                });

                if (connected && tempClient != null)
                {
                    _sshClient = tempClient; // 只有完全连上后才交给全局对象
                    Dispatcher.Invoke(() =>
                    {
                        ConnectBtn.Content = "已连接 (点击断开)";
                        ConnectBtn.Background = Brushes.Green;
                    });
                    return true;
                }
            }
            catch
            {
                // 外部兜底，确保万无一失
            }
            finally
            {
                // 如果没连上，必须立即销毁 tempClient 释放本地 Socket
                if (_sshClient != tempClient && tempClient != null)
                {
                    try { tempClient.Dispose(); } catch { }
                }
            }

            return false;
        }
        /// 安装显卡补丁逻辑：执行脚本 step2
        /// </summary>
        private async void InstallPatchBtn_Click(object sender, RoutedEventArgs e)
        {
            // 1. 检查 SSH 连接状态
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: 请先连接服务器。");
                return;
            }

            AppendLog("🚀 正在启动显卡补丁安装程序 (step2)...");
            bool isScriptFinished = false;

            try
            {
                await Task.Run(async () =>
                {
                    // 1. 赋予执行权限
                    _sshClient.RunCommand("chmod +x /root/step2");

                    // 2. 启动脚本并获取 PID
                    // 使用 nohup 确保脚本在后台运行，即使 SSH 断开也不会中断
                    var startCmd = _sshClient.CreateCommand("nohup /root/step2 > /root/patch.log 2>&1 & echo $!");
                    string pid = startCmd.Execute().Trim();

                    if (string.IsNullOrEmpty(pid))
                    {
                        AppendLog("❌ 错误: 无法获取安装进程 PID。");
                        return;
                    }

                    AppendLog("⏳ 正在安装补丁 (脚本执行完毕或系统重启前不会重复提醒)...");

                    // 3. 循环监测 PID 是否存在
                    bool isProcessRunning = true;
                    while (isProcessRunning)
                    {
                        await Task.Delay(3000);

                        // 检测进程文件夹是否存在
                        var checkCmd = _sshClient.RunCommand($"if [ -d /proc/{pid} ]; then echo \"running\"; fi");

                        if (checkCmd.Result.Trim() != "running")
                        {
                            isProcessRunning = false;
                            isScriptFinished = true;
                        }

                        // 如果 SSH 已经断开（说明脚本触发了重启）
                        if (!_sshClient.IsConnected)
                        {
                            isProcessRunning = false;
                            isScriptFinished = true;
                        }
                    }
                });
            }
            catch { isScriptFinished = true; }

            if (isScriptFinished)
            {
                AppendLog("✅ 检测到显卡补丁脚本执行完毕！");
                // 调用您原有的自动重连逻辑
                await StartAutoReconnect();
            }
        }

        //修改所有配置
        private async void updatehw_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            // --- 1. 获取原始输入 (完全保留) ---
            string cpuRaw = CpuInput.Text.Trim();
            string ramRaw = RamInput.Text.Trim();
            string vramRaw = VramInput.Text.Trim();
            string diskRaw = DiskInput.Text.Trim();

            // 预处理变量 (完全还原你的初始变量声明)
            int r = 0;
            int ramMb = 0;
            string disk1Size = ""; // C盘
            string disk2Size = ""; // D盘

            bool updateRam = !string.IsNullOrEmpty(ramRaw) && int.TryParse(ramRaw, out r);
            if (updateRam) ramMb = r * 1024;

            // --- 磁盘解析逻辑：完全还原你提供的 "64/200", "/200", "64" 支持 ---
            bool updateDisk1 = false;
            bool updateDisk2 = false;

            if (!string.IsNullOrEmpty(diskRaw))
            {
                if (diskRaw.Contains("/"))
                {
                    var parts = diskRaw.Split('/');
                    string p1 = parts[0].Trim();
                    string p2 = (parts.Length > 1) ? parts[1].Trim() : "";

                    // 如果 / 前面有数字，改C盘 (完全保留你的逻辑)
                    if (!string.IsNullOrEmpty(p1))
                    {
                        disk1Size = p1.ToLower().Replace("g", "");
                        updateDisk1 = true;
                    }
                    // 如果 / 后面有数字，改D盘 (完全保留你的逻辑)
                    if (!string.IsNullOrEmpty(p2))
                    {
                        disk2Size = p2.ToLower().Replace("g", "");
                        updateDisk2 = true;
                    }
                }
                else
                {
                    // 如果没有斜杠，默认认为填的是C盘
                    disk1Size = diskRaw.ToLower().Replace("g", "");
                    updateDisk1 = true;
                }
            }

            string[] excludedIds = { "100", "777", "666" };
            string masterDiskPath = "/mnt/game-disk/master/game-disk.qcow2";
            AppendLog("⚙️ 正在执行全局同步 (局部修改模式)...");

            bool hasError = false; // 用于追踪是否有关键步骤失败

            try
            {
                await Task.Run(async () =>
                {
                    // --- 步骤 2: 动态调整物理母盘大小 (仅限D盘) ---
                    if (updateDisk2)
                    {
                        AppendLog($"📂 正在检查物理母盘并尝试扩容: {masterDiskPath} -> {disk2Size}G");
                        var resizeCmd = _sshClient.RunCommand($"qemu-img resize {masterDiskPath} {disk2Size}G");

                        if (resizeCmd.ExitStatus == 0)
                            AppendLog($"✅ 物理母盘已调整为 {disk2Size}G。");
                        else
                            AppendLog($"⚠️ 磁盘调整提示: {resizeCmd.Error.Trim()}");
                    }

                    // --- 步骤 3: 修改显存 TOML (完全还原你的计算逻辑) ---
                    if (!string.IsNullOrEmpty(vramRaw) && double.TryParse(vramRaw, out double vramNum))
                    {
                        // 1. 将 GB 转换为字节数 (Bytes) 用于总显存
                        long fb = (long)(vramNum * 1024 * 1024 * 1024);

                        // 2. 固定预留 128MB (128 * 1024 * 1024 = 134217728 字节)
                        long fbr = 128L * 1024 * 1024;

                        // 3. 转换为十进制字符串（字节单位）
                        string fbValue = fb.ToString();
                        string fbrValue = fbr.ToString();

                        string tomlPath = "/etc/vgpu_unlock/profile_override.toml";

                        // 4. 使用 sed 替换配置
                        var c1 = _sshClient.RunCommand($"[ -f {tomlPath} ] && sed -i 's/framebuffer = .*/framebuffer = {fbValue}/' {tomlPath}");
                        var c2 = _sshClient.RunCommand($"[ -f {tomlPath} ] && sed -i 's/framebuffer_reservation = .*/framebuffer_reservation = {fbrValue}/' {tomlPath}");

                        if (c1.ExitStatus != 0 || c2.ExitStatus != 0)
                        {
                            AppendLog("❌ 显存配置同步失败！");
                            hasError = true;
                        }
                        else
                        {
                            AppendLog($"✅ 显存同步完成: 总额 {vramNum}G, 预留 {fbrValue} 字节 (128M)");
                        }
                    }

                    // --- 步骤 4: 遍历并修改所有子机的 .conf ---
                    var getIdsCmd = _sshClient.RunCommand("ls /etc/pve/qemu-server/ | sed 's/.conf//g'");
                    if (string.IsNullOrEmpty(getIdsCmd.Result.Trim())) return;

                    string[] vmidList = getIdsCmd.Result.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    int updateCount = 0;

                    foreach (var vmid in vmidList)
                    {
                        if (excludedIds.Contains(vmid)) continue;

                        string confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                        System.Text.StringBuilder cmdBuilder = new System.Text.StringBuilder();

                        if (!string.IsNullOrEmpty(cpuRaw))
                            cmdBuilder.Append($"sed -i 's/^cores:.*/cores: {cpuRaw}/' {confPath} && ");

                        if (updateRam)
                            cmdBuilder.Append($"sed -i 's/^memory:.*/memory: {ramMb}/' {confPath} && ");

                        if (updateDisk1)
                            cmdBuilder.Append($"sed -i '/sata0:/ s/size=[0-9]\\+G/size={disk1Size}G/g' {confPath} && ");

                        if (updateDisk2)
                            cmdBuilder.Append($"sed -i '/sata1:/ s/size=[0-9]\\+G/size={disk2Size}G/g' {confPath} && ");

                        if (cmdBuilder.Length > 0)
                        {
                            string finalCmd = cmdBuilder.ToString().TrimEnd(' ', '&');
                            var res = _sshClient.RunCommand(finalCmd);
                            if (res.ExitStatus != 0)
                            {
                                AppendLog($"❌ 虚拟机 {vmid} 更新失败: {res.Error}");
                                hasError = true;
                            }
                            else
                            {
                                updateCount++;
                            }
                        }
                    }

                    AppendLog($"🎉 同步完成！共更新 {updateCount} 台虚拟机。");

                    // --- 5. 重启判定：如果没有关键报错，则提示重启 ---
                    if (!hasError)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            var result = MessageBox.Show("同步成功！是否立即重启服务器并自动等待重连？", "需要重启", MessageBoxButton.YesNo, MessageBoxImage.Question);
                            if (result == MessageBoxResult.Yes)
                            {
                                AppendLog("🚀 正在发送重启指令...");
                                // 在后台启动重启与重连监测
                                _ = Task.Run(async () =>
                                {
                                    try { _sshClient.RunCommand("reboot"); } catch { }

                                    AppendLog("💤 服务器重启中，开始监测回连状态 (预计需 2-5 分钟)...");

                                    bool reconnected = false;
                                    for (int i = 1; i <= 30; i++) // 尝试 30 次，每次间隔 10 秒
                                    {
                                        await Task.Delay(10000);
                                        try
                                        {
                                            if (!_sshClient.IsConnected) _sshClient.Connect();
                                            if (_sshClient.IsConnected) { reconnected = true; break; }
                                        }
                                        catch { /* 继续等待开机 */ }
                                        AppendLog($"正在尝试重连... ({i}/30)");
                                    }

                                    if (reconnected) AppendLog("✨ 服务器已重新连接，配置生效！");
                                    else AppendLog("❌ 自动重连超时，请手动检查服务器。");
                                });
                            }
                        });
                    }
                    else { AppendLog("⚠️ 发现操作错误，已取消重启提示，请检查红色报错。"); }
                });
            }
            catch (Exception ex) { AppendLog($"❌ 批量更新失败: {ex.Message}"); }
        }
        //创建母机
        private async void cjmuji_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            // 1. 参数解析 (例如 64/200)
            string cpu = CpuInput.Text.Trim();
            string ramGb = RamInput.Text.Trim();
            string vramGb = VramInput.Text.Trim();
            string diskRaw = DiskInput.Text.Trim();

            string disk1 = "64", disk2 = "200";
            if (diskRaw.Contains("/"))
            {
                var parts = diskRaw.Split('/');
                disk1 = parts[0].Trim();
                disk2 = parts[1].Trim();
            }

            AppendLog($"🚀 启动母机流程: C盘 {disk1}G, D盘(检测中) {disk2}G...");

            try
            {
                await Task.Run(async () =>
                {
                    // --- 步骤 1: 执行基础脚本 (muji.sh) ---
                    if (int.TryParse(ramGb, out int ramMb)) ramMb *= 1024;
                    string sedCmd = $@"sed -i 's/CORES:-\([0-9]\+\)/CORES:-{cpu}/' /root/muji.sh && " +
                                     $@"sed -i 's/MEMORY:-\([0-9]\+\)/MEMORY:-{ramMb}/' /root/muji.sh && " +
                                     $@"sed -i 's/DISK_SIZE:-\([0-9]\+\)/DISK_SIZE:-{disk1}/' /root/muji.sh";
                    _sshClient.RunCommand(sedCmd);
                    _sshClient.RunCommand("chmod +x /root/muji.sh && printf '\\n\\n\\n\\n\\n' | /root/muji.sh");

                    // --- 步骤 2: 创建显卡配置文件 ---
                    _sshClient.RunCommand("mkdir -p /etc/vgpu_unlock/");

                    // 准备完整的 TOML 内容，包含你要求的所有参数
                    string tomlContent = @"[profile.nvidia-46]
num_displays = 1
display_width = 1920
display_height = 1080
max_pixels = 2073600
cuda_enabled = 1
frl_enabled = 0
framebuffer = 0x1A000000
framebuffer_reservation = 0x6000000
pci_device_id = 0x1c31
pci_id = 0x1c310000";

                    // 使用 cat 重新生成完整文件，确保不再因为 sed 导致格式错乱
                    _sshClient.RunCommand($"cat <<EOF > /etc/vgpu_unlock/profile_override.toml\n{tomlContent}\nEOF");

                    AppendLog("✅ 显卡创建完毕");
                    // --- 步骤 3: 【核心逻辑】判断并创建 D 盘文件 ---
                    string imgPath = "/mnt/game-disk/master/game-disk.qcow2";
                    _sshClient.RunCommand("mkdir -p /mnt/game-disk/master/ && chmod 777 /mnt/game-disk/master/");

                    // 只有文件不存在时，才执行 qemu-img create
                    AppendLog("📂 正在检查 D 盘物理文件...");
                    string checkAndCreate = $"if [ ! -f {imgPath} ]; then " +
                                            $"echo '文件不存在，正在创建...'; " +
                                            $"qemu-img create -f qcow2 {imgPath} {disk2}G; " +
                                            "else echo '文件已存在，跳过创建直接挂载'; fi";
                    var runRes = _sshClient.RunCommand(checkAndCreate);
                    AppendLog(runRes.Result.Contains("已存在") ? "ℹ️ 检测到现有 D 盘文件，将直接保留数据挂载。" : $"✅ 已新建 {disk2}G D 盘文件。");

                    // --- 步骤 4: 强制修改 .conf 文件确保挂载 ---
                    AppendLog("🔗 正在强制注入磁盘挂载配置...");

                    // 识别存储 ID
                    string storage = _sshClient.RunCommand("pvesm status | grep -E 'local|local-lvm' | awk 'NR==1{print $1}'").Result.Trim();
                    if (string.IsNullOrEmpty(storage)) storage = "local";

                    string confPath = "/etc/pve/qemu-server/100.conf";
                    // 构造配置行
                    string sata0Line = $"{storage}:100/vm-100-disk-0.qcow2,discard=on,size={disk1}G,ssd=1";
                    string sata1Line = $"{imgPath},format=qcow2,size={disk2}G";

                    // 强制写入配置：先删掉原有的 sata0/sata1 行，再追加
                    _sshClient.RunCommand($"sed -i '/^sata0:/d' {confPath} && echo 'sata0: {sata0Line}' >> {confPath}");
                    _sshClient.RunCommand($"sed -i '/^sata1:/d' {confPath} && echo 'sata1: {sata1Line}' >> {confPath}");
                    // 确保引导顺序
                    _sshClient.RunCommand($"qm set 100 --boot order=sata0");

                    AppendLog("✅ 母机 100 部署确认成功。");

                    // --- 步骤 5: 重启 ---
                    _sshClient.CreateCommand("reboot").BeginExecute();
                    await Task.Delay(2000); _sshClient.Disconnect();
                });
                await StartAutoReconnect();
            }
            catch (Exception ex) { AppendLog($"❌ 失败: {ex.Message}"); }
        }
        //转化为模板
        private async void cjtemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            AppendLog("🔍 正在执行安全克隆流程 (避开物理路径冲突)...");

            try
            {
                await Task.Run(async () =>
                {
                    string conf100 = "/etc/pve/qemu-server/100.conf";
                    string imgPath = "/mnt/game-disk/master/game-disk.qcow2";

                    // --- 1. 临时摘除 100 号母鸡的 sata1 配置 ---
                    // 将包含物理路径的那一行先保存到变量，然后从配置文件中删除
                    AppendLog("✂️ 临时脱离母机 D 盘引用以确保克隆兼容性...");
                    _sshClient.RunCommand($"sed -i '/sata1:/d' {conf100}");
                    _sshClient.RunCommand("qm unlock 100");

                    // --- 2. 清理并克隆 ---
                    _sshClient.RunCommand("qm destroy 777 --purge --skiplock");

                    AppendLog("🐑 正在克隆纯净系统镜像 (777)...");
                    var cloneCmd = _sshClient.RunCommand("qm clone 100 777 --name Template-777 --full 1");

                    if (cloneCmd.ExitStatus != 0)
                    {
                        AppendLog($"❌ 克隆失败: {cloneCmd.Error}");
                        // 失败了也要尝试把母鸡的 D 盘接回去
                        _sshClient.RunCommand($"echo 'sata1: {imgPath},format=qcow2' >> {conf100}");
                        return;
                    }

                    // --- 3. 还原母鸡 (100) 的 D 盘挂载 ---
                    AppendLog("🔗 正在恢复母机 D 盘挂载...");
                    _sshClient.RunCommand($"echo 'sata1: {imgPath},format=qcow2' >> {conf100}");

                    // --- 4. 转化为模板 ---
                    AppendLog("💾 正在将 777 转化为模板...");
                    _sshClient.RunCommand("qm template 777");

                    AppendLog("🎉 [SUCCESS] 777 纯净模板制作成功！");
                    AppendLog("ℹ️ 母机 (100) 已恢复 D 盘挂载，模板 (777) 仅含系统盘。");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 执行异常: {ex.Message}");
            }
        }
        private async void startmuji_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            AppendLog("⚡ 正在尝试启动母机 (ID: 100)...");
            try
            {
                await Task.Run(() =>
                {
                    // 1. 尝试解锁（防止异常关机导致的锁定）
                    _sshClient.RunCommand("qm unlock 100");

                    // 2. 执行启动命令
                    var res = _sshClient.RunCommand("qm start 100");

                    if (res.ExitStatus == 0)
                        AppendLog("✅ 母机 100 启动命令发送成功。");
                    else if (res.Error.Contains("already running"))
                        AppendLog("ℹ️ 母机 100 已经在运行中。");
                    else
                        AppendLog($"❌ 启动失败: {res.Error}");
                });
            }
            catch (Exception ex) { AppendLog($"❌ 异常: {ex.Message}"); }
        }
        //删除所有虚拟机
        private async void delall_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: 请先连接服务器。");
                return;
            }

            // 弹窗确认，防止误操作
            var result = System.Windows.MessageBox.Show("警告：此操作将删除 PVE 节点上所有的虚拟机（包括母机、模板及克隆机）！\n\n是否继续？", "危险操作确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            AppendLog("🧨 正在启动全自动清空流程...");

            try
            {
                await Task.Run(() =>
                {
                    // --- 步骤 1: 获取所有虚拟机 ID ---
                    // 遍历 /etc/pve/qemu-server/ 目录下的所有 .conf 文件名
                    // ls 命令配合 sed 只提取数字部分 (即 VMID)
                    var getIdsCmd = _sshClient.RunCommand("ls /etc/pve/qemu-server/ | sed 's/.conf//g'");
                    string output = getIdsCmd.Result.Trim();

                    if (string.IsNullOrEmpty(output))
                    {
                        AppendLog("ℹ️ 未发现任何虚拟机，无需清理。");
                        return;
                    }

                    string[] vmidList = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    AppendLog($"🔍 发现 {vmidList.Length} 台机器，准备强制销毁...");

                    // --- 步骤 2: 遍历并执行销毁 ---
                    foreach (var vmid in vmidList)
                    {
                        AppendLog($"🗑️ 正在删除 VMID: {vmid} ...");

                        // 1. 先强制解锁，防止因为正在运行或备份导致的删除失败
                        _sshClient.RunCommand($"qm unlock {vmid}");

                        // 2. 停止机器 (防止正在运行)
                        _sshClient.RunCommand($"qm stop {vmid} --skiplock");

                        // 3. 彻底摧毁
                        // --purge 会同时删除 PVE 存储池内的磁盘镜像 (如 C 盘)
                        var delRes = _sshClient.RunCommand($"qm destroy {vmid} --purge --skiplock");

                        if (delRes.ExitStatus == 0)
                            AppendLog($"✅ VM {vmid} 已彻底抹除。");
                        else
                            AppendLog($"⚠️ VM {vmid} 删除时遇到问题: {delRes.Error}");
                    }

                    AppendLog("🏁 [FINISH] 所有虚拟机已从 PVE 中移除。");
                    AppendLog("ℹ️ 注意：物理目录 /mnt/game-disk/ 下的文件已保留。");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 执行异常: {ex.Message}");
            }
        }
        //防爆盘
        private async void fangbao_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: 请先连接服务器。");
                return;
            }

            // 安全二次确认
            var result = System.Windows.MessageBox.Show(
                "该操作将清空 /var/lib/vz/images 目录下的所有虚拟机镜像文件！\n这通常用于清理残留垃圾，防止系统盘爆满。\n\n确定要继续吗？",
                "清理确认",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            AppendLog("🧹 正在启动系统盘防爆清理...");

            try
            {
                await Task.Run(() =>
                {
                    // 1. 定义目标目录
                    string targetDir = "/var/lib/vz/images";

                    // 2. 检查目录是否存在
                    var checkDir = _sshClient.RunCommand($"[ -d {targetDir} ] && echo 'exists'");
                    if (checkDir.Result.Trim() != "exists")
                    {
                        AppendLog($"ℹ️ 目录 {targetDir} 不存在，无需清理。");
                        return;
                    }

                    // 3. 执行强制递归删除
                    // 使用 -rf 强制删除该目录下的所有子目录和文件
                    AppendLog($"🗑️ 正在清理 {targetDir} 中的所有残留镜像...");
                    var delCmd = _sshClient.RunCommand($"rm -rf {targetDir}/*");

                    if (delCmd.ExitStatus == 0)
                    {
                        AppendLog("✅ [SUCCESS] 镜像目录已清空，系统盘空间已释放。");
                    }
                    else
                    {
                        AppendLog($"❌ 清理过程中出错: {delCmd.Error}");
                    }

                    // 4. 额外清理：清理 PVE 临时任务日志（可选，进一步防爆）
                    _sshClient.RunCommand("rm -rf /var/log/pve/tasks/*");
                    AppendLog("🧹 任务日志已同步清理。");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 执行异常: {ex.Message}");
            }
        }
        //清理缓存
        private async void clear_cache_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            AppendLog("🧹 正在深度清理服务器系统缓存...");

            try
            {
                await Task.Run(() =>
                {
                    // --- 1. 释放 Linux 内核内存缓存 ---
                    // sync: 将内存中未写入磁盘的数据强制写入，防止丢失
                    // echo 3 > /proc/sys/vm/drop_caches: 释放 PageCache, dentries 和 inodes
                    AppendLog("🧠 正在回收系统空闲内存...");
                    _sshClient.RunCommand("sync && echo 3 > /proc/sys/vm/drop_caches");

                    // --- 2. 清理 APT 包管理器缓存 ---
                    // clean: 删除所有已下载的包文件
                    // autoremove: 删除不再需要的依赖包
                    AppendLog("📦 正在清理安装包残留...");
                    _sshClient.RunCommand("apt-get clean && apt-get autoremove -y");

                    // --- 3. 清理系统临时目录 ---
                    // 只删除 1 天前的文件，保证安全性
                    AppendLog("📂 正在清理临时临时文件...");
                    _sshClient.RunCommand("find /tmp -type f -atime +1 -delete");

                    // --- 4. 刷新日志缓存 ---
                    // 清理已归档的旧日志
                    _sshClient.RunCommand("journalctl --vacuum-time=1d");

                    AppendLog("✨ [SUCCESS] 系统缓存清理完毕，当前内存状态已重置。");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 清理失败: {ex.Message}");
            }
        }
        //同步D盘
        private async void tbvm_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            // 1. 获取动态磁盘参数
            string diskRaw = DiskInput.Text.Trim();
            string disk2Size = "200G";
            if (diskRaw.Contains("/")) disk2Size = diskRaw.Split('/')[1].Trim() + "G";

            // 排除列表
            string[] excludedIds = { "100", "777", "666" };

            AppendLog("🔄 启动 D 盘全自动同步流程 (关机 -> 重置 -> 开机)...");

            try
            {
                await Task.Run(async () =>
                {
                    // --- 步骤 1: 路径准备 ---
                    string masterDisk = "/mnt/game-disk/master/game-disk.qcow2";
                    string vmDiskDir = "/mnt/game-disk/vm";
                    _sshClient.RunCommand($"mkdir -p {vmDiskDir}");

                    // 获取所有 VM ID
                    var getIdsCmd = _sshClient.RunCommand("ls /etc/pve/qemu-server/ | sed 's/.conf//g'");
                    string[] vmidList = getIdsCmd.Result.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var vmid in vmidList)
                    {
                        if (excludedIds.Contains(vmid)) continue;

                        AppendLog($"🛠️ 正在处理虚拟机 {vmid} ...");

                        // --- 步骤 2: 【核心步骤】安全关机 ---
                        // 先解锁防止任务挂起，然后停止机器以释放磁盘句柄
                        _sshClient.RunCommand($"qm unlock {vmid}");
                        AppendLog($"   - 正在停止 VM {vmid} ...");
                        _sshClient.RunCommand($"qm stop {vmid} --skiplock");

                        // --- 步骤 3: 差分磁盘处理 (参考你的 fg.sh) ---
                        string diffDiskPath = $"{vmDiskDir}/{vmid}-game-disk.qcow2";
                        string confPath = $"/etc/pve/qemu-server/{vmid}.conf";

                        // 只有关机后，rm -f 才能确保彻底删除旧的差分盘文件
                        _sshClient.RunCommand($"rm -f {diffDiskPath}");

                        // 创建新的差分镜像，指向母盘
                        var createDiff = _sshClient.RunCommand($"qemu-img create -f qcow2 -b {masterDisk} -o backing_fmt=qcow2 {diffDiskPath} {disk2Size}");

                        if (createDiff.ExitStatus != 0)
                        {
                            AppendLog($"   ❌ VM {vmid} 磁盘创建失败，跳过。");
                            continue;
                        }

                        // --- 步骤 4: 配置文件注入 ---
                        // 更新配置文件中的 sata1 路径
                        string diskEntry = $"sata1: {diffDiskPath},format=qcow2,size={disk2Size}";
                        _sshClient.RunCommand($"sed -i '/^sata1:/d' {confPath} && echo '{diskEntry}' >> {confPath}");

                        // --- 步骤 5: 【核心步骤】重新启动 ---
                        AppendLog($"   - VM {vmid} 同步完成，正在启动...");
                        _sshClient.RunCommand($"qm start {vmid}");
                    }

                    AppendLog("🏁 [FINISH] 所有子机同步重置完毕。");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"❌ tbvm 流程异常: {ex.Message}");
            }
        }
        //批量克隆
        private async void StartBatchBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                AppendLog("❌ 错误: SSH 未连接。");
                return;
            }

            // 1. UI 参数获取与校验
            if (!int.TryParse(StartId.Text, out int startId) ||
                !int.TryParse(EndId.Text, out int endId) ||
                !int.TryParse(CurrentIndex.Text, out int startIndex))
            {
                AppendLog("❌ 错误: 请检查输入的 ID 或序号。");
                return;
            }

            string prefix = VmName.Text.Trim();
            int total = endId - startId + 1;
            Random rnd = new Random();

            try
            {
                await Task.Run(async () =>
                {
                    // --- A. 环境前置核对 ---
                    var checkMnt = _sshClient.RunCommand("[ -d /mnt/game-disk ] && echo 'OK' || echo 'MISS'");
                    if (checkMnt.Result.Trim() != "OK")
                    {
                        AppendLog("❌ 严重停止: 宿主机未检测到 /mnt/game-disk！D盘挂载环境异常。");
                        return;
                    }

                    var checkWpDir = _sshClient.RunCommand("[ -d /root/wallpapers ] && echo 'OK' || echo 'MISS'");
                    bool canInjectWallpaper = (checkWpDir.Result.Trim() == "OK");
                    if (!canInjectWallpaper) AppendLog("ℹ️ 提示: 未发现 /root/wallpapers，将跳过壁纸修改。");

                    // --- B. 批量循环执行 ---
                    for (int i = 0; i < total; i++)
                    {
                        int currentVmId = startId + i;
                        int currentOrderIdx = startIndex + i;

                        Dispatcher.Invoke(() => CurrentIndex.Text = currentOrderIdx.ToString());
                        AppendLog($"----------------------------------------");

                        // 1. ID 占用检测逻辑
                        var checkIdRes = _sshClient.RunCommand($"qm status {currentVmId} 2>&1");
                        if (!checkIdRes.Result.Contains("not exist"))
                        {
                            AppendLog($"ℹ️ 跳过: VM ID {currentVmId} 已存在。");
                            continue;
                        }

                        AppendLog($"📦 [1/4] 正在克隆虚拟机: {currentVmId} ({prefix}-{currentOrderIdx})...");

                        // 2. 克隆 (从模板 777)
                        var cloneRes = _sshClient.RunCommand($"qm clone 777 {currentVmId} --name {prefix}-{currentOrderIdx} --full 0");
                        if (cloneRes.ExitStatus != 0)
                        {
                            AppendLog($"❌ 克隆失败 (ID {currentVmId}): {cloneRes.Error}");
                            continue;
                        }

                        // 3. D 盘差分挂载逻辑 (sata1)
                        AppendLog($"🔗 [2/4] 配置外部 D 盘差分挂载...");
                        string vmDDir = "/mnt/game-disk/vm";
                        string masterD = "/mnt/game-disk/master/game-disk.qcow2";
                        string vmDPath = $"{vmDDir}/{currentVmId}-game-disk.qcow2";
                        string confPath = $"/etc/pve/qemu-server/{currentVmId}.conf";

                        string diskCmd = $"mkdir -p {vmDDir} && " +
                                         $"qemu-img create -f qcow2 -b {masterD} -F qcow2 {vmDPath} 200G && " +
                                         $"sed -i '/sata1:/d' {confPath} && " +
                                         $"echo 'sata1: {vmDPath},format=qcow2' >> {confPath}";
                        _sshClient.RunCommand(diskCmd);

                        // 4. 深度硬件随机化注入 (包含 BIOS/主板全套序列号伪装)
                        AppendLog($"🎲 [3/4] 注入全套唯一硬件指纹...");
                        ApplyUltraAntiVirt(currentVmId, rnd);

                        // 5. 壁纸注入 (适配 Directory 模式路径)
                        if (canInjectWallpaper)
                        {
                            var checkImg = _sshClient.RunCommand($"[ -f /root/wallpapers/{currentOrderIdx}.jpg ] && echo 'OK' || echo 'MISS'");
                            if (checkImg.Result.Trim() == "OK")
                            {
                                AppendLog($"🖼️ [4/4] 正在注入壁纸: {currentOrderIdx}.jpg");
                                string targetDisk = $"/var/lib/vz/images/{currentVmId}/vm-{currentVmId}-disk-0.qcow2";
                                InjectUserWallpaper(currentVmId, currentOrderIdx, targetDisk);
                            }
                        }

                        // 6. 启动
                        _sshClient.RunCommand($"qm start {currentVmId}");
                        AppendLog($"⚡ VM {currentVmId} 配置完成并启动。");
                    }
                    AppendLog("🎉 [SUCCESS] 批量任务执行完毕！");
                });
            }
            catch (Exception ex) { AppendLog($"❌ 致命异常: {ex.Message}"); }
        }

        private void ApplyUltraAntiVirt(int vmid, Random rnd)
        {
            string confPath = $"/etc/pve/qemu-server/{vmid}.conf";

            // 1. 扩大的品牌池与主板型号联动
            var brandPool = new[] {
            new { Brand = "ASUS", Boards = new[] { "PRIME Z790-P", "ROG STRIX Z690-E", "TUF GAMING B760M-PLUS" } },
            new { Brand = "MSI", Boards = new[] { "MAG Z790 TOMAHAWK", "MPG Z890 CARBON", "PRO Z790-A WIFI" } },
            new { Brand = "Gigabyte", Boards = new[] { "Z790 AORUS ELITE", "B760 AORUS MASTER", "Z690 UD" } }
        };
            string[] cpuPool = { "14th Gen Intel(R) Core(TM) i9-14900K", "13th Gen Intel(R) Core(TM) i9-13900K", "Intel(R) Core(TM) i7-14700K" };
            string[] ramPool = { "Kingston", "Samsung", "Corsair", "G.Skill", "SK Hynix" };

            var selectedBrand = brandPool[rnd.Next(brandPool.Length)];
            string randVendor = selectedBrand.Brand;
            string randMB = selectedBrand.Boards[rnd.Next(selectedBrand.Boards.Length)];
            string randCpu = cpuPool[rnd.Next(cpuPool.Length)];
            string randRam = ramPool[rnd.Next(ramPool.Length)];

            // 2. 深度随机化参数 (BIOS/SN/UUID/MAC)
            string sn = "SN" + rnd.Next(1000000, 9999999).ToString();
            string biosSn = "BIOS-" + rnd.Next(100000, 999999).ToString();
            string ramSn = rnd.Next(10000000, 99999999).ToString("X");
            string uuid = Guid.NewGuid().ToString();
            string mac = string.Format("AC:12:03:{0:X2}:{1:X2}:{2:X2}", rnd.Next(256), rnd.Next(256), rnd.Next(256));
            string randDate = $"{rnd.Next(1, 13):D2}/{rnd.Next(1, 28):D2}/{rnd.Next(2022, 2025)}";
            string biosVer = $"{rnd.Next(1, 30)}.{rnd.Next(1, 99)}";
            int randSpeed = (rnd.Next(0, 2) == 0) ? 5200 : 6000;

            // 3. 构造全套 SMBIOS 注入 Args
            string args = "args: -cpu host,kvm=off,hv_vendor_id=intel,hv_relaxed,hv_spinlocks=0x1fff,hv_vapic,hv_time,hv_reset,hv_vpindex,hv_runtime,hv_synic,hv_stimer,hv_ips,hv_frequencies,host-phys-bits=true,hypervisor=off,+pmu,+pdpe1gb " +
                          $"-smbios type=0,vendor=\"{randVendor}\",version=\"{biosVer}\",date=\"{randDate}\" " +
                          $"-smbios type=1,manufacturer=\"{randVendor}\",product=\"QGE5GU9\",version=\"2024.1\",serial=\"{biosSn}\",uuid={uuid} " +
                          $"-smbios type=2,manufacturer=\"{randVendor}\",product=\"{randMB}\",version=\"rev1.0\",serial=\"{sn}\" " +
                          $"-smbios type=3,manufacturer=\"{randVendor}\",serial=\"{sn}\",asset=\"Chassis-Asset-Tag\" " +
                          $"-smbios type=4,version=\"{randCpu}\",manufacturer=Intel,max-speed={randSpeed},current-speed={randSpeed} " +
                          $"-smbios type=17,manufacturer=\"{randRam}\",loc_pfx=\"DDR5\",speed={randSpeed},serial=\"{ramSn}\",part=\"MOD-{rnd.Next(100, 999)}\" " +
                          "-smbios type=11,value=\"Modern Preload\"";

            // 4. 执行写入 (保留 memory: 原始行，不进行 sed 覆盖)
            string cmd = $@"
    sed -i '/net0:/d' {confPath} && 
    echo 'net0: e1000={mac},bridge=vmbr0,firewall=1' >> {confPath} && 
    sed -i '/smbios1:/d' {confPath} && 
    echo 'smbios1: uuid={uuid}' >> {confPath} && 
    sed -i '/vmgenid:/d' {confPath} && 
    echo 'vmgenid: {Guid.NewGuid()}' >> {confPath} && 
    sed -i '/args:/d' {confPath} && 
    echo '{args}' >> {confPath}";

            _sshClient.RunCommand(cmd);
        }

        private void InjectUserWallpaper(int vmid, int order, string diskPath)
        {
            string winDir = "Users/Administrator/Pictures/壁纸";
            string injectCmd = $@"
    modprobe nbd max_part=8 && 
    qemu-nbd --connect=/dev/nbd0 {diskPath} && 
    sleep 2 && 
    mkdir -p /mnt/tmp_{vmid} && 
    (mount -o remove_hiberfile /dev/nbd0p2 /mnt/tmp_{vmid} || mount -o remove_hiberfile /dev/nbd0p3 /mnt/tmp_{vmid}) && 
    mkdir -p ""/mnt/tmp_{vmid}/{winDir}"" && 
    cp -f /root/wallpapers/{order}.jpg ""/mnt/tmp_{vmid}/{winDir}/1.jpg"" && 
    chmod 777 ""/mnt/tmp_{vmid}/{winDir}/1.jpg"" && 
    sync && umount /mnt/tmp_{vmid} && 
    qemu-nbd --disconnect /dev/nbd0 && 
    rmdir /mnt/tmp_{vmid}";

            _sshClient.RunCommand(injectCmd);
        }
        //批量管理工具
        // --- A. 通用 ID 解析助手 ---
        private List<int> GetTargetIdList()
        {
            List<int> ids = new List<int>();
            string input = TargetIds.Text.Trim(); // 对应 XAML 中的 TargetIds
            if (string.IsNullOrEmpty(input)) return ids;

            try
            {
                // 分离逗号和中文逗号
                string[] parts = input.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.Contains("-")) // 处理 101-105 范围
                    {
                        var range = part.Trim().Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out int s) && int.TryParse(range[1], out int e))
                        {
                            for (int i = s; i <= e; i++) ids.Add(i);
                        }
                    }
                    else if (int.TryParse(part.Trim(), out int id)) // 处理单个 ID
                    {
                        ids.Add(id);
                    }
                }
            }
            catch (Exception ex) { AppendLog($"❌ 解析错误: {ex.Message}"); }
            return ids.Distinct().ToList();
        }

        // --- B. 批量管理各功能实现 ---

        // 批量开机
        private async void BtnBatchStart_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetTargetIdList();
            if (ids.Count == 0) return;
            await Task.Run(() => {
                foreach (var id in ids)
                {
                    AppendLog($"⚡ 启动 VM {id}...");
                    _sshClient.RunCommand($"qm start {id}");
                }
                AppendLog("✅ 批量启动指令完成。");
            });
        }

        // 批量停止
        private async void BtnBatchStop_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetTargetIdList();
            if (ids.Count == 0) return;
            await Task.Run(() => {
                foreach (var id in ids)
                {
                    AppendLog($"🛑 停止 VM {id}...");
                    _sshClient.RunCommand($"qm stop {id}");
                }
                AppendLog("✅ 批量停止指令完成。");
            });
        }

        // 批量重启
        private async void BtnBatchReboot_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetTargetIdList();
            if (ids.Count == 0) return;
            await Task.Run(() => {
                foreach (var id in ids)
                {
                    AppendLog($"🔄 重启 VM {id}...");
                    _sshClient.RunCommand($"qm reboot {id}");
                }
                AppendLog("✅ 批量重启指令完成。");
            });
        }

        // 批量删除 (核心逻辑：仅 ID 100 删 D 盘)
        private async void BtnBatchDelete_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetTargetIdList();
            if (ids.Count == 0) return;

            var confirm = MessageBox.Show($"确定删除这 {ids.Count} 个虚拟机吗？\n警告：ID 为 100 的 D 盘文件将被永久清理！",
                                        "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            await Task.Run(() => {
                foreach (var id in ids)
                {
                    AppendLog($"🗑️ 处理 VM {id}...");

                    // 1. 强制停止并销毁虚拟机配置
                    _sshClient.RunCommand($"qm stop {id}");
                    _sshClient.RunCommand($"qm destroy {id} --purge");

                    // 2. 指向性删除判断
                    if (id == 100)
                    {
                        AppendLog($"⚠️ ID 为 100，正在清理物理 D 盘文件...");
                        string vmDPath = $"/mnt/game-disk/vm/{id}-game-disk.qcow2";
                        _sshClient.RunCommand($"rm -f {vmDPath}");
                    }
                    else
                    {
                        AppendLog($"ℹ️ VM {id} 配置已删，D 盘镜像保留。");
                    }
                }
                AppendLog("🎉 批量删除任务执行完毕。");
            });
        }
        // --- 新增：选择游戏预设时立即触发 ---
        private void GamePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 1. 获取选中的项
            if (GamePresetCombo.SelectedItem is ComboBoxItem gci)
            {
                // 2. 获取文本内容
                string gamePreset = gci.Content.ToString() ?? "";

                // 3. 逻辑实现：直接根据包含的关键词修改 UI
                // 使用 Contains 比用 == 更稳，能避开空格或斜杠导致的匹配失败
                if (gamePreset.Contains("CSGO"))
                {
                    CpuInput.Text = "4";
                    DiskInput.Text = "64/200";
                    RamInput.Text = "8";
                    VramInput.Text = "0.8";
                }
                else if (gamePreset.Contains("SCUM"))
                {
                    CpuInput.Text = "8";
                    DiskInput.Text = "80/200";
                    RamInput.Text = "12";
                    VramInput.Text = "1.5";
                }
            }
        }
        //应用预设配置
        private async void BtnApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            // 1. 读取选项
            string imageChoice = "";
            if (ImageSelectCombo.SelectedItem is ComboBoxItem ici) imageChoice = ici.Content.ToString() ?? "";
            // 过滤功能项，只处理具体镜像文件
            if (!string.IsNullOrEmpty(imageChoice) && imageChoice != "上传" && imageChoice != "远程下载")
            {
                if (_sshClient != null && _sshClient.IsConnected)
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            string configPath = "/etc/pve/qemu-server/100.conf";
                            string targetKey = "ide2:";
                            string newLine = $"ide2: local:iso/{imageChoice},media=cdrom";

                            // A. 先从服务器读取当前文件内容
                            var rawContent = _sshClient.RunCommand($"cat {configPath}").Result;

                            // B. 按行拆分，处理掉 Windows/Linux 换行符差异
                            var lines = rawContent.Split(new[] { "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries).ToList();

                            bool found = false;
                            // C. 查找并替换 ide2 这一行
                            for (int i = 0; i < lines.Count; i++)
                            {
                                if (lines[i].Trim().StartsWith(targetKey))
                                {
                                    lines[i] = newLine;
                                    found = true;
                                    break;
                                }
                            }

                            // D. 如果原文件里没有 ide2，则在末尾添加
                            if (!found) lines.Add(newLine);

                            // E. 重新组合成纯净的 Linux 格式字符串
                            string finalContent = string.Join("\n", lines);

                            // F. 使用 Base64 安全传输并覆写文件 (最稳妥的方法)
                            var base64Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(finalContent));
                            var writeResult = _sshClient.RunCommand($"echo '{base64Content}' | base64 -d > {configPath}");

                            if (writeResult.ExitStatus == 0)
                            {
                                AppendLog($"✅ 配置已应用：镜像 [{imageChoice}] 已通过文件覆写绑定。");
                            }
                            else
                            {
                                AppendLog($"❌ 配置文件写入失败: {writeResult.Error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"❌ 访问母机配置文件出错: {ex.Message}");
                        }
                    });
                }
                else
                {
                    AppendLog("⚠️ SSH 未连接，无法更新服务器配置。");
                }
            }
            else if (imageChoice == "上传" || imageChoice == "远程下载")
            {
                AppendLog("💡 请先完成上传或下载，并选中具体的镜像文件名后再应用。");
            }
        }
        //  AppendLog 方法
        private void AppendLog(string message)
        {
            if (LogBox == null) return;
            // 使用 BeginInvoke，让后台线程发送完日志后立即继续工作，不等待 UI 响应
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogBox.ScrollToEnd();
            }));
        }
        // 【整合核心】统一处理镜像任务：支持本地上传和远程下载
        private async Task HandleImageTaskAsync(string mode, string source, string destPath)
        {
            // 使用 Task.Run 确保整个过程在后台运行，不卡死 UI
            await Task.Run(async () =>
            {
                try
                {
                    if (mode == "UPLOAD")
                    {
                        AppendLog($"🚀 [本地上传] 准备发送: {Path.GetFileName(source)}");

                        // 独立连接，防止通道堵塞
                        using (var sftp = new SftpClient(_lastHost, _lastUser, _lastPass))
                        {
                            sftp.Connect();
                            using (var fs = File.OpenRead(source))
                            {
                                long totalBytes = fs.Length;
                                DateTime lastUpdate = DateTime.MinValue;

                                // 执行上传
                                sftp.UploadFile(fs, destPath, (uploaded) => {
                                    // 限流更新进度
                                    if ((DateTime.Now - lastUpdate).TotalSeconds >= 1 || (long)uploaded == (long)totalBytes)
                                    {
                                        double pct = (double)uploaded / totalBytes * 100;
                                        AppendLog($"⬆️ 上传进度: {pct:F1}% ({(uploaded / 1024.0 / 1024.0):F1}MB)");
                                        lastUpdate = DateTime.Now;
                                    }
                                });
                            }
                            sftp.Disconnect();
                        }
                    }
                    else if (mode == "DOWNLOAD")
                    {
                        AppendLog($"🌐 [远程下载] 通知服务器获取: {source}");
                        // 发送下载指令，nohup 保证后台运行
                        _sshClient.RunCommand($"nohup curl -L -o \"{destPath}\" \"{source}\" > /dev/null 2>&1 &");

                        // 监控逻辑
                        bool isRunning = true;
                        while (isRunning)
                        {
                            await Task.Delay(3000); // 每3秒查一次
                            var ps = _sshClient.RunCommand($"ps aux | grep \"curl\" | grep \"{Path.GetFileName(destPath)}\" | grep -v grep").Result;
                            var size = _sshClient.RunCommand($"stat -c%s \"{destPath}\" 2>/dev/null").Result;

                            if (string.IsNullOrWhiteSpace(ps))
                            {
                                isRunning = false;
                                break;
                            }
                            double mb = (double.TryParse(size, out var s) ? s : 0) / 1024 / 1024;
                            AppendLog($"☁️ 远程下载中... 当前大小: {mb:F2} MB");
                        }
                    }

                    // 任务结束逻辑
                    AppendLog("🔄 任务完成，正在同步镜像列表...");
                    await RefreshRemoteImagesAsync();

                    // 自动在 UI 上选中新文件
                    Dispatcher.BeginInvoke(new Action(() => {
                        ImageSelectCombo.Text = Path.GetFileName(destPath);
                    }));
                    AppendLog("✅ 镜像处理成功！");
                }
                catch (Exception ex)
                {
                    AppendLog($"❌ 任务出错: {ex.Message}");
                }
            });
        }
        private void ImageSelectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 1. 拦截：如果是内部更新或没选中，直接返回
            if (_isInternalUpdating || ImageSelectCombo.SelectedItem == null) return;

            // 2. 只定义一次变量
            var selectedItem = (ComboBoxItem)ImageSelectCombo.SelectedItem;
            string myChoice = selectedItem.Content.ToString(); // 换个名字避开冲突

            // --- 情况 1：上传 ---
            if (myChoice == "上传")
            {
                var ofd = new OpenFileDialog { Filter = "镜像文件|*.iso;*.qcow2;*.img" };
                if (ofd.ShowDialog() == true)
                {
                    string remotePath = "/var/lib/vz/template/iso/" + Path.GetFileName(ofd.FileName);
                    _ = HandleImageTaskAsync("UPLOAD", ofd.FileName, remotePath);
                }
            }
            // --- 情况 2：远程下载 ---
            else if (myChoice == "远程下载")
            {
                string url = Microsoft.VisualBasic.Interaction.InputBox("输入下载地址:", "远程下载", "https://");
                if (!string.IsNullOrWhiteSpace(url) && url.Length > 10)
                {
                    try
                    {
                        string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                        if (string.IsNullOrEmpty(fileName)) fileName = "downloaded_img.iso";
                        string remotePath = "/var/lib/vz/template/iso/" + fileName;
                        _ = HandleImageTaskAsync("DOWNLOAD", url, remotePath);
                    }
                    catch
                    {
                        AppendLog("❌ URL 格式不正确");
                    }
                }
            }
            // --- 情况 3：选中了具体镜像文件 ---
            else
            {
                _selectedRemoteImageName = myChoice;
                AppendLog($"🎯 已选中镜像: {myChoice}");
            }
        }
        // 新增方法：用于刷新下拉列表中的镜像文件
        private async Task RefreshRemoteImagesAsync()
        {
            if (_sshClient == null || !_sshClient.IsConnected) return;

            await Task.Run(() => {
                try
                {
                    var cmd = _sshClient.RunCommand("ls -1 /var/lib/vz/template/iso/");
                    var files = cmd.Result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Where(f => f.Contains(".")).ToList();

                    Dispatcher.BeginInvoke(new Action(() => {
                        // 1. 开启保护：告诉程序这是内部更新
                        _isInternalUpdating = true;

                        ImageSelectCombo.Items.Clear();
                        ImageSelectCombo.Items.Add(new ComboBoxItem { Content = "上传" });
                        ImageSelectCombo.Items.Add(new ComboBoxItem { Content = "远程下载" });
                        foreach (var f in files) ImageSelectCombo.Items.Add(new ComboBoxItem { Content = f.Trim() });

                        // 2. 结束保护：恢复监听
                        _isInternalUpdating = false;
                    }));
                }
                catch { _isInternalUpdating = false; }
            });
        }

        private void InitBtn_Click_1(object sender, RoutedEventArgs e)
        {

        }

        private void cjtemplate_Click_1(object sender, RoutedEventArgs e)
        {

        }
    }
}
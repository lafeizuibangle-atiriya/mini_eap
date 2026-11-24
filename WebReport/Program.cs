using System.Text;
using System.Text.Json;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 数据源：MySQL 优先，连不上降级读主控端落盘的 JSONL 文件，保证演示随时可用
string connStr = Environment.GetEnvironmentVariable("MINIEAP_MYSQL")
    ?? "Server=localhost;Port=3306;Database=mini_eap;Uid=root;Pwd=123456;";
string fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MiniEapHost", "bin", "Debug", "net8.0-windows", "logs", "minieap_events.jsonl");

async Task<object> QueryAsync(Func<MySqlConnection, Task<object>> query, Func<Task<object>> fallbackFn)
{
    try
    {
        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();
        return await query(conn);
    }
    catch
    {
        return await fallbackFn();
    }
}

List<JsonElement> ReadFileEvents()
{
    var list = new List<JsonElement>();
    if (!File.Exists(fallback)) return list;
    foreach (var line in File.ReadAllLines(fallback))
    {
        if (line.Trim().Length == 0) continue;
        try { list.Add(JsonSerializer.Deserialize<JsonElement>(line)); } catch { }
    }
    return list;
}

// ---------- API ----------

app.MapGet("/api/runs", () => QueryAsync(async conn =>
{
    var rows = new List<object>();
    await using var cmd = new MySqlCommand(
        "SELECT h.id, h.eqp_id, h.recipe, h.start_time, h.end_time, h.result " +
        "FROM run_history h ORDER BY h.id DESC LIMIT 100", conn);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        rows.Add(new { id = r.GetInt64(0), eqp = r.GetString(1), recipe = r.GetString(2),
                       start = r.GetDateTime(3).ToString("yyyy-MM-dd HH:mm:ss"),
                       end = r.GetDateTime(4).ToString("yyyy-MM-dd HH:mm:ss"), result = r.GetString(5) });
    return Results.Json(rows);
}, () =>
{
    var rows = new List<object>();
    foreach (var e in ReadFileEvents())
    {
        if (e.TryGetProperty("kind", out var k) && k.GetString() == "run_done")
            rows.Add(new { eqp = e.GetProperty("Device").GetString(), recipe = e.GetProperty("Recipe").GetString(),
                           result = e.GetProperty("Result").GetString(),
                           end = DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("Ts").GetInt64()).ToString("yyyy-MM-dd HH:mm:ss") });
    }
    return Task.FromResult<object>(Results.Json(rows));
}));

app.MapGet("/api/alarms", () => QueryAsync(async conn =>
{
    var rows = new List<object>();
    await using var cmd = new MySqlCommand(
        "SELECT a.id, a.eqp_id, a.alarm_code, a.level, a.alarm_text, a.alarm_value, a.occurred_at, a.is_cleared " +
        "FROM alarm_history a ORDER BY a.id DESC LIMIT 100", conn);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        rows.Add(new { id = r.GetInt64(0), eqp = r.GetString(1), code = r.GetString(2), level = r.GetString(3),
                       text = r.GetString(4), value = r.IsDBNull(5) ? 0 : r.GetDouble(5),
                       at = r.GetDateTime(6).ToString("yyyy-MM-dd HH:mm:ss"), cleared = r.GetBoolean(7) });
    return Results.Json(rows);
}, () =>
{
    var rows = new List<object>();
    foreach (var e in ReadFileEvents())
    {
        if (e.TryGetProperty("kind", out var k) && (k.GetString() == "alarm" || k.GetString() == "alarm_clear"))
            rows.Add(new { eqp = e.GetProperty("Device").GetString(), code = e.GetProperty("Code").GetString(),
                           text = k.GetString() == "alarm_clear" ? "报警复位" : e.GetProperty("Text").GetString(),
                           at = DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("Ts").GetInt64()).ToString("yyyy-MM-dd HH:mm:ss"),
                           cleared = k.GetString() == "alarm_clear" });
    }
    return Task.FromResult<object>(Results.Json(rows));
}));

app.MapGet("/api/stats", () => QueryAsync(async conn =>
{
    object stats = new { };
    await using (var cmd = new MySqlCommand("CALL sp_alarm_stats()", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync())
            stats = new { eqp = r.GetString(0), total = r.GetInt64(1), cleared = r.GetInt64(2), active = r.GetInt64(3) };
    return Results.Json(stats);
}, () =>
{
    int alarm = 0, clear = 0, run = 0;
    foreach (var e in ReadFileEvents())
    {
        if (!e.TryGetProperty("kind", out var k)) continue;
        string s = k.GetString();
        if (s == "alarm") alarm++;
        else if (s == "alarm_clear") clear++;
        else if (s == "run_done") run++;
    }
    return Task.FromResult<object>(Results.Json(new { summary = true, alarm, clear, run }));
}));

// ---------- 报表页 ----------

app.MapGet("/", async () =>
{
    var runs = await QueryRunsJson();
    var alarms = await QueryAlarmsJson();
    string html = $@"<!DOCTYPE html>
<html lang=""zh""><head><meta charset=""utf-8""><title>Mini-EAP 生产报表</title>
<style>
 body {{ font-family: 'Microsoft YaHei', sans-serif; margin: 24px; color: #222; background:#f7f8fa; }}
 h1 {{ font-size: 20px; }} h2 {{ font-size: 16px; margin-top: 28px; }}
 table {{ border-collapse: collapse; width: 100%; background: #fff; }}
 th, td {{ border: 1px solid #ccc; padding: 6px 10px; font-size: 13px; }}
 th {{ background: #2f5496; color: #fff; }}
 tr:nth-child(even) td {{ background: #f4f6fb; }}
 .ok {{ color: #188038; }} .bad {{ color: #c5221f; }}
 .note {{ color:#888; font-size:12px; }}
</style></head><body>
<h1>Mini-EAP 生产报表（半导体设备自动化原型）</h1>
<p class=""note"">数据来源：MySQL 存储过程查询；MySQL 不可用时自动降级读取主控端本地事件文件。</p>
<h2>报警记录（近 100 条）</h2>{alarms}
<h2>生产运行记录（近 100 条）</h2>{runs}
</body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

async Task<string> QueryRunsJson()
{
    var res = await QueryAsync(async conn =>
    {
        var rows = new List<string[]>();
        await using var cmd = new MySqlCommand(
            "SELECT h.id, h.eqp_id, h.recipe, h.start_time, h.end_time, h.result FROM run_history h ORDER BY h.id DESC LIMIT 100", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new[] { r.GetInt64(0).ToString(), r.GetString(1), r.GetString(2),
                             r.GetDateTime(3).ToString("MM-dd HH:mm:ss"), r.GetDateTime(4).ToString("MM-dd HH:mm:ss"), r.GetString(5) });
        return rows;
    }, () =>
    {
        var rows = new List<string[]>();
        foreach (var e in ReadFileEvents())
            if (e.TryGetProperty("kind", out var k) && k.GetString() == "run_done")
                rows.Add(new[] { "-", e.GetProperty("Device").GetString()!, e.GetProperty("Recipe").GetString()!,
                                 "-", DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("Ts").GetInt64()).ToString("MM-dd HH:mm:ss"),
                                 e.GetProperty("Result").GetString()! });
        return Task.FromResult<object>(rows);
    });
    return BuildTable(new[] { "ID", "机台", "Recipe", "开始", "结束", "结果" }, (List<string[]>)res,
        row => row[5] == "OK" ? "<span class='ok'>OK</span>" : "<span class='bad'>ABORT</span>");
}

async Task<string> QueryAlarmsJson()
{
    var res = await QueryAsync(async conn =>
    {
        var rows = new List<string[]>();
        await using var cmd = new MySqlCommand(
            "SELECT a.id, a.eqp_id, a.alarm_code, a.level, a.alarm_text, a.alarm_value, a.occurred_at, a.is_cleared FROM alarm_history a ORDER BY a.id DESC LIMIT 100", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new[] { r.GetInt64(0).ToString(), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                             r.IsDBNull(5) ? "-" : r.GetDouble(5).ToString("0.0"),
                             r.GetDateTime(6).ToString("MM-dd HH:mm:ss"), r.GetBoolean(7) ? "已复位" : "未复位" });
        return rows;
    }, () =>
    {
        var rows = new List<string[]>();
        foreach (var e in ReadFileEvents())
            if (e.TryGetProperty("kind", out var k))
            {
                string s = k.GetString();
                if (s == "alarm")
                    rows.Add(new[] { "-", e.GetProperty("Device").GetString()!, e.GetProperty("Code").GetString()!,
                                     e.GetProperty("Level").GetString()!, e.GetProperty("Text").GetString()!,
                                     e.GetProperty("Value").GetDouble().ToString("0.0"),
                                     DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("Ts").GetInt64()).ToString("MM-dd HH:mm:ss"), "未复位" });
                else if (s == "alarm_clear")
                    rows.Add(new[] { "-", e.GetProperty("Device").GetString()!, e.GetProperty("Code").GetString()!,
                                     "-", "报警复位", "-",                                      DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("Ts").GetInt64()).ToString("MM-dd HH:mm:ss"), "已复位" });
            }
        return Task.FromResult<object>(rows);
    });
    return BuildTable(new[] { "ID", "机台", "代码", "级别", "内容", "数值", "时间", "状态" }, (List<string[]>)res,
        row => row[7] == "已复位" ? "<span class='ok'>已复位</span>" : "<span class='bad'>未复位</span>");
}

string BuildTable(string[] headers, List<string[]> rows, Func<string[], string> lastCell)
{
    if (rows.Count == 0) return "<p class='note'>暂无数据（先启动主控端与模拟器产生事件）</p>";
    var sb = new StringBuilder("<table><tr>");
    foreach (var h in headers) sb.Append($"<th>{h}</th>");
    sb.Append("</tr>");
    foreach (var row in rows)
    {
        sb.Append("<tr>");
        for (int i = 0; i < row.Length - 1; i++) sb.Append($"<td>{row[i]}</td>");
        sb.Append($"<td>{lastCell(row)}</td></tr>");
    }
    sb.Append("</table>");
    return sb.ToString();
}

app.Run();

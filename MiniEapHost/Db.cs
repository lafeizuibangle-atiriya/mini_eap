using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiniEapHost
{
    /// <summary>存储抽象：MySQL（ADO.NET 调存储过程）优先，MySQL 不可用时降级为本地 JSON 文件。</summary>
    public interface IDbStore
    {
        string Mode { get; }
        Task AddAlarmAsync(AlarmEvent al);
        Task ClearAlarmAsync(AlarmEvent al);
        Task FinishRunAsync(RunDoneEvent rd);
    }

    /// <summary>
    /// MySQL 实现：全部写操作走存储过程（sp_add_alarm / sp_clear_alarm / sp_finish_run），
    /// 与 database/02_procedures.sql 一一对应。
    /// </summary>
    public class MySqlStore : IDbStore
    {
        public string Mode => "MySQL";
        private readonly string _connStr;

        public MySqlStore(string connStr) => _connStr = connStr;

        public async Task AddAlarmAsync(AlarmEvent al)
        {
            using var conn = new MySqlConnector.MySqlConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = new MySqlConnector.MySqlCommand("sp_add_alarm", conn)
            { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_eqp_id", al.Device);
            cmd.Parameters.AddWithValue("p_alarm_code", al.Code);
            cmd.Parameters.AddWithValue("p_level", al.Level);
            cmd.Parameters.AddWithValue("p_text", al.Text);
            cmd.Parameters.AddWithValue("p_value", al.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task ClearAlarmAsync(AlarmEvent al)
        {
            using var conn = new MySqlConnector.MySqlConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = new MySqlConnector.MySqlCommand("sp_clear_alarm", conn)
            { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_eqp_id", al.Device);
            cmd.Parameters.AddWithValue("p_alarm_code", al.Code);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task FinishRunAsync(RunDoneEvent rd)
        {
            using var conn = new MySqlConnector.MySqlConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = new MySqlConnector.MySqlCommand("sp_finish_run", conn)
            { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_eqp_id", rd.Device);
            cmd.Parameters.AddWithValue("p_recipe", rd.Recipe);
            cmd.Parameters.AddWithValue("p_result", rd.Result);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>本地文件兜底实现：每条事件追加为 JSON 行（logs/minieap_events.jsonl），演示无 MySQL 也能跑通。</summary>
    public class FileStore : IDbStore
    {
        public string Mode => "File";
        private static readonly object _lock = new();
        private readonly string _path;

        public FileStore(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
        }

        public Task AddAlarmAsync(AlarmEvent al) => WriteAsync(new { kind = "alarm", al.Device, al.Code, al.Level, al.Text, al.Value, al.Ts });
        public Task ClearAlarmAsync(AlarmEvent al) => WriteAsync(new { kind = "alarm_clear", al.Device, al.Code, al.Ts });
        public Task FinishRunAsync(RunDoneEvent rd) => WriteAsync(new { kind = "run_done", rd.Device, rd.Recipe, rd.Result, rd.Ts });

        private Task WriteAsync(object payload)
        {
            return Task.Run(() =>
            {
                lock (_lock)
                    File.AppendAllText(_path, JsonSerializer.Serialize(payload) + "\n", Encoding.UTF8);
            });
        }
    }

    /// <summary>存储工厂：尝试连 MySQL，2 秒连不上就用文件兜底，保证任何时候都能演示。</summary>
    public static class StoreFactory
    {
        public static async Task<IDbStore> CreateAsync(string connStr, string fallbackPath)
        {
            try
            {
                var conn = new MySqlConnector.MySqlConnection(connStr);
                var t = conn.OpenAsync();
                if (await Task.WhenAny(t, Task.Delay(2000)) == t)
                {
                    await conn.CloseAsync();
                    return new MySqlStore(connStr);
                }
            }
            catch { /* 降级 */ }
            return new FileStore(fallbackPath);
        }
    }
}

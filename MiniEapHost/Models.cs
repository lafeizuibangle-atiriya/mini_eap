using System;

namespace MiniEapHost
{
    /// <summary>设备状态事件（模拟器每 2 秒上报一次）</summary>
    public class EquipmentStatus
    {
        public string Type { get; set; }
        public string Device { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string Recipe { get; set; }
        public int Progress { get; set; }
        public double ChamberTemp { get; set; }
        public double Pressure { get; set; }
        public long Ts { get; set; }
    }

    /// <summary>报警 / 报警复位事件</summary>
    public class AlarmEvent
    {
        public string Type { get; set; }      // alarm / alarm_clear
        public string Device { get; set; }
        public string Code { get; set; }
        public string Text { get; set; }
        public string Level { get; set; }
        public double Value { get; set; }
        public long Ts { get; set; }
    }

    /// <summary>运行结束事件</summary>
    public class RunDoneEvent
    {
        public string Type { get; set; }
        public string Device { get; set; }
        public string Recipe { get; set; }
        public string Result { get; set; }    // OK / ABORT
        public long Ts { get; set; }
    }

    /// <summary>命令应答</summary>
    public class AckMessage
    {
        public string Type { get; set; }
        public string Cmd { get; set; }
        public string Device { get; set; }
        public bool Ok { get; set; }
        public string Msg { get; set; }
    }
}

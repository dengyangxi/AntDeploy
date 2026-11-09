using System;

namespace AntDeploy.Core
{
    /// <summary>
    /// 日志级别（与 Agent 端一致，便于协议对齐）
    /// </summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4
    }

    /// <summary>
    /// 与具体日志框架（NLog 等）解耦的日志抽象。
    /// AntDeployWinform 可用 NLog 适配器，Web 端可接入 SignalR / UI 实时日志。
    /// </summary>
    public interface ILog
    {
        void Info(string msg);
        void Warn(string msg);
        void Error(string msg);
        void Debug(string msg);
    }

    /// <summary>
    /// 空日志实现，便于在不需要日志的单元测试或后台场景中使用。
    /// </summary>
    public class NullLog : ILog
    {
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
        public void Debug(string msg) { }
    }

    /// <summary>
    /// 将 LoggerModel 风格的日志回调包装成 ILog，方便把协议层日志汇入上层。
    /// </summary>
    public class ActionLog : ILog
    {
        private readonly Action<LogLevel, string> _sink;
        public ActionLog(Action<LogLevel, string> sink) { _sink = sink; }
        public void Info(string msg) => _sink?.Invoke(LogLevel.Info, msg);
        public void Warn(string msg) => _sink?.Invoke(LogLevel.Warn, msg);
        public void Error(string msg) => _sink?.Invoke(LogLevel.Error, msg);
        public void Debug(string msg) => _sink?.Invoke(LogLevel.Debug, msg);
    }
}

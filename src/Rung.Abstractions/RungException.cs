namespace Rung.Abstractions;

/// <summary>Rung 抛出的所有异常的基类。</summary>
public class RungException : Exception
{
    /// <summary>创建一个异常。</summary>
    public RungException(string message) : base(message) { }

    /// <summary>创建一个带内部异常的异常。</summary>
    public RungException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>点位地址无法解析，或与声明的数据类型不匹配。属于配置错误，重试无意义。</summary>
public sealed class AddressFormatException : RungException
{
    /// <summary>创建一个地址格式异常。</summary>
    public AddressFormatException(string address, string reason)
        : base($"地址 \"{address}\" 无效：{reason}")
    {
        Address = address;
        Reason = reason;
    }

    /// <summary>出错的原始地址字符串。</summary>
    public string Address { get; }

    /// <summary>失败原因。</summary>
    public string Reason { get; }
}

/// <summary>报文不符合协议规范：长度不足、字段值非法、设备返回错误码等。</summary>
public sealed class ProtocolException : RungException
{
    /// <summary>创建一个协议异常。</summary>
    public ProtocolException(string message) : base(message) { }

    /// <summary>创建一个带内部异常的协议异常。</summary>
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

﻿using System.Reflection;
using System.Text.RegularExpressions;

namespace Datask.Common.Utilities;

public static class EventArgsExtensions
{
    public static void FireStatusEvent<TType>(this object caller,
        EventHandler<StatusEventArgs<TType>>? handler,
        TType statusType,
        object? metadata = null,
        string? message = null)
        where TType : Enum
    {
        EventHandler<StatusEventArgs<TType>>? handlerCopy = handler;
        if (handlerCopy is not null)
        {
            var args = new StatusEventArgs<TType>(statusType, message, metadata);
            handlerCopy(caller, args);
        }
    }

    public static void Fire<TType>(this EventHandler<StatusEventArgs<TType>>? handler,
        TType statusType,
        object? metadata = null,
        string? message = null)
        where TType : Enum
    {
        EventHandler<StatusEventArgs<TType>>? handlerCopy = handler;
        if (handlerCopy is not null)
        {
            var args = new StatusEventArgs<TType>(statusType, message, metadata);
            handlerCopy(null, args);
        }
    }
}

public sealed class StatusEventArgs<TType> : EventArgs
    where TType : Enum
{
    public StatusEventArgs(TType statusType, string? message = null, object? metadata = null)
    {
        StatusType = statusType;

        IDictionary<string, object>? metadataBag = null;
        if (metadata is not null)
        {
            metadataBag = metadata.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(pi => pi.CanRead && pi.GetIndexParameters().Length == 0)
                .ToDictionary(pi => pi.Name, pi => pi.GetValue(metadata) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }

        if (message is null)
            Message = null;
        else if (metadataBag is null)
            Message = message;
        else
        {
            Message = Patterns.PlaceholderPattern.Replace(message, match =>
                metadataBag.TryGetValue(match.Groups[1].Value, out object? obj) ? obj.ToString()! : match.Value);
        }
    }

    public TType StatusType { get; }

    public string? Message { get; }
}

internal static class Patterns
{
    //NOTE: Declared in a separate class as static fields are not shared between different instances
    //of that generic type (Sonar analyzer S2743)
    internal static readonly Regex PlaceholderPattern = new(@"\{(\w+)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}

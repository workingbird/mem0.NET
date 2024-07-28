﻿namespace mem0.Core.Model;

public class VectorData
{
    public object Id { get; set; } // 向量 ID

    public float Score { get; set; } // 向量

    public IReadOnlyList<float> Vector { get; set; }
    
    public Dictionary<string, object> MetaData { get; set; } // 有效载荷


    public string? Text
    {
        get
        {
            if (MetaData.TryGetValue("data", out var data))
            {
                return data.ToString();
            }

            return string.Empty;
        }
    } // 文本
}
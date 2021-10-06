﻿using System;

namespace Metrics.EventCounters.Linux.CPU
{
    public class ProcessorInfo
    {
        public static readonly int ProcessorCount = GetProcessorCount();

        public static int GetProcessorCount()
        {
            return Environment.ProcessorCount;
        }
    }

}

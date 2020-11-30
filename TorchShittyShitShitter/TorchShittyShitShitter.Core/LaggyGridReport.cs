﻿namespace TorchShittyShitShitter.Core
{
    /// <summary>
    /// Carry around a laggy grid's metadata.
    /// </summary>
    public class LaggyGridReport
    {
        public LaggyGridReport(long gridId, double mspf)
        {
            GridId = gridId;
            Mspf = mspf;
        }

        public long GridId { get; }
        public double Mspf { get; }
    }
}
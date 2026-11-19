namespace xISOBurn
{
    /// <summary>The words the shared engine (xisoburn/engine) uses for things that differ between the apps.</summary>
    internal static class Platform
    {
        /// <summary>Where Blu-ray key files are added (AACS log, no KEYDB.cfg).</summary>
        public const string AddKeysHint = "Add them with the Blu-ray keys button";
        /// <summary>How to get the drive out of a bad state (disc scan).</summary>
        public const string ResetDriveHint = "eject the disc or power-cycle the drive";
        /// <summary>What to do when the destination is full.</summary>
        public const string OtherDestinationHint = "Choose another destination folder";
    }
}

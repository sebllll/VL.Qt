namespace VL.Qt.Elements
{
    internal static class PointerMasks
    {
        /// <summary>Qt's default of acceptedDevices (all device types, also those without an enum value here).</summary>
        private const int allDevices = 0x7FFFFFFF;

        public const PointerButtons DefaultButtons = PointerButtons.Left;
        public const PointerDevices DefaultDevices = PointerDevices.All;

        /// <summary>The value for ACCEPTED_DEVICES: <see cref="PointerDevices.All"/> maps to Qt's AllDevices.</summary>
        public static int ToQt(PointerDevices devices) => devices == PointerDevices.All ? allDevices : (int)devices;
    }
}

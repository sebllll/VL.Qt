namespace VL.Qt.Wire
{
    /// <summary>
    /// Mirror of the constants of src/VL.Qt.Native/vlqt_test.h (the test exports, not part of the protocol). Naming as
    /// <see cref="Protocol"/>: VLQT_&lt;GROUP&gt;_&lt;UPPER_SNAKE&gt; maps to TestProtocol.&lt;Group&gt;.&lt;PascalCase&gt;.
    /// The unit test ProtocolDriftTests fails on any mismatch in name or value.
    /// </summary>
    internal static class TestProtocol
    {
        /// <summary>Mouse event types of vlqt_test_send_mouse / vlqt_test_queue_mouse.</summary>
        internal static class Mouse
        {
            public const int Move = 1;
            public const int Press = 2;
            public const int Release = 3;
            public const int Leave = 4;
            public const int PressRight = 5;
            public const int ReleaseRight = 6;
            /// <summary>Right button synthesized by the OS from touch (Windows press-and-hold).</summary>
            public const int PressRightSystem = 7;
            public const int ReleaseRightSystem = 8;
        }

        /// <summary>Touch point events of vlqt_test_send_touch / vlqt_test_queue_touch.</summary>
        internal static class Touch
        {
            public const int Press = 1;
            public const int Move = 2;
            public const int Release = 3;
        }

        /// <summary>Modes of vlqt_test_device_lost.</summary>
        internal static class DeviceLost
        {
            /// <summary>Real removal of the shared device; needs VLQT_TEST_D3D11ON12=1 before the device is created.</summary>
            public const int Remove = 1;
            /// <summary>Real loss through a GPU hang (TDR, whole GPU); manual checks only.</summary>
            public const int Hang = 2;
        }

        /// <summary>Resource kind of vlqt_res_create for vlqt_test_res_start; the product kinds are <see cref="Protocol.ResKind"/>.</summary>
        internal static class ResKind
        {
            /// <summary>Decode sleeps in a worker, upload uses budget (vlqt_test_res_start).</summary>
            public const int Test = 1;
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Utils
{
    public static class ButtonUtils
    {
        // 整数値に変更（0〜31の範囲内）
        public const byte ButtonGrab_L = 0;
        public const byte ButtonGrab_R = 1;
        public const byte ButtonPunch_R = 2;
        public const byte ButtonPunch_L = 3;
        public const byte ButtonJump = 4;
        public const byte ButtonDash = 5;
        public const byte ButtonCrouch = 6;
        public const byte ButtonToggleRotationMode = 7;
    }
}

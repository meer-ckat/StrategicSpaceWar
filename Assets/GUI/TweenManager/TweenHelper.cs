using UnityEngine;

namespace IMGUI
{
    public static class TweenHelper
    {
        // =========================================================
        // SINE
        // =========================================================

        public static float EaseInSine(float x)
        {
            return 1f - Mathf.Cos((x * Mathf.PI) / 2f);
        }

        public static float EaseOutSine(float x)
        {
            return Mathf.Sin((x * Mathf.PI) / 2f);
        }

        public static float EaseInOutSine(float x)
        {
            return -(Mathf.Cos(Mathf.PI * x) - 1f) / 2f;
        }


        // =========================================================
        // QUAD
        // =========================================================

        public static float EaseInQuad(float x)
        {
            return x * x;
        }

        public static float EaseOutQuad(float x)
        {
            return 1f - (1f - x) * (1f - x);
        }

        public static float EaseInOutQuad(float x)
        {
            return x < 0.5f
                ? 2f * x * x
                : 1f - Mathf.Pow(-2f * x + 2f, 2f) / 2f;
        }


        // =========================================================
        // BACK
        // =========================================================

        public static float EaseInBack(float x)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;

            return c3 * x * x * x
                 - c1 * x * x;
        }

        public static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;

            return 1f
                 + c3 * Mathf.Pow(x - 1f, 3f)
                 + c1 * Mathf.Pow(x - 1f, 2f);
        }

        public static float EaseInOutBack(float x)
        {
            const float c1 = 1.70158f;
            const float c2 = c1 * 1.525f;

            return x < 0.5f
                ? Mathf.Pow(2f * x, 2f)
                    * ((c2 + 1f) * 2f * x - c2)
                    / 2f
                : (
                    Mathf.Pow(2f * x - 2f, 2f)
                    * ((c2 + 1f) * (x * 2f - 2f) + c2)
                    + 2f
                  ) / 2f;
        }


        // =========================================================
        // EXPO
        // =========================================================

        public static float EaseInExpo(float x)
        {
            return x == 0f
                ? 0f
                : Mathf.Pow(2f, 10f * x - 10f);
        }

        public static float EaseOutExpo(float x)
        {
            return x == 1f
                ? 1f
                : 1f - Mathf.Pow(2f, -10f * x);
        }

        public static float EaseInOutExpo(float x)
        {
            if (x == 0f)
                return 0f;

            if (x == 1f)
                return 1f;

            return x < 0.5f
                ? Mathf.Pow(2f, 20f * x - 10f) / 2f
                : (2f - Mathf.Pow(2f, -20f * x + 10f)) / 2f;
        }


        // =========================================================
        // THE CONSTITUTION
        // =========================================================

        /// <summary>
        /// Converts a value inside min~max into normalized animation x (0~1).
        /// 모든 애니메이션은 XCalculator의 지배를 받는다.
        /// </summary>
        public static float XCalculator(
            float value,
            float min,
            float max)
        {
            return Mathf.InverseLerp(min, max, value);
        }
    }
}
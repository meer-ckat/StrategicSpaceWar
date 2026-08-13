using System;
using System.Collections.Generic;
using UnityEngine;

namespace IMGUI
{
    public static class GUITween
    {
        private static readonly Dictionary<GUIItem, Action<GUIItem, float>> running = new();
        private static readonly Dictionary<GUIItem, Action<GUIItem, float>> fading = new();
        private static readonly Dictionary<GUIItem, Action<GUIItem, float>> scaling = new();

        private static void ApplyPos(GUIItem item, Vector2 pos)
        {
            if (item is GUIGroup)
                item.SetRect(new Rect(pos, item.Size));
            else
                item.SetPos(pos);
        }

        public static void MoveTo(
            this GUIItem item,
            Vector2 to,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            KillMove(item);

            ease ??= TweenHelper.EaseOutSine;

            float time = 0f;
            bool begun = false;
            Vector2 from = default;
            Action<GUIItem, float> handler = null;

            handler = (gui, dt) =>
            {
                time += dt;
                float local = time - delay;

                if (local < 0f)
                    return;

                // DrawItemLocal이 Draw 중에 Rect를 잠시 밀어둔 상태에서
                // MoveTo가 호출되면(버튼 클릭 등) from이 어긋난 좌표로 고정된다.
                // 첫 적용 프레임(Update)에 잡아서 진짜 위치를 사용한다.
                if (!begun)
                {
                    begun = true;
                    from = gui.Pos;
                }

                float x = duration > 0f
                    ? TweenHelper.XCalculator(local, 0f, duration)
                    : 1f;

                ApplyPos(gui, Vector2.LerpUnclamped(from, to, ease(x)));

                if (x < 1f)
                    return;

                ApplyPos(gui, to);

                gui.whenTick -= handler;
                running.Remove(gui);

                onComplete?.Invoke();
            };

            item.whenTick += handler;
            running[item] = handler;
        }

        public static void MoveIn(
            this GUIItem item,
            Vector2 offset,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            Vector2 target = item.Pos;

            ApplyPos(item, target + offset);
            item.MoveTo(target, duration, delay, ease, onComplete);
        }

        public static void MoveOut(
            this GUIItem item,
            Vector2 offset,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            item.MoveTo(item.Pos + offset, duration, delay, ease, onComplete);
        }

        public static void FadeTo(
            this GUIItem item,
            float to,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            KillFade(item);

            float from = item.Opacity;
            float time = 0f;

            to = Mathf.Clamp01(to);
            ease ??= TweenHelper.EaseInOutSine;

            Action<GUIItem, float> handler = null;

            handler = (gui, dt) =>
            {
                time += dt;
                float local = time - delay;

                if (local < 0f)
                    return;

                float x = duration > 0f
                    ? TweenHelper.XCalculator(local, 0f, duration)
                    : 1f;

                gui.Opacity = Mathf.LerpUnclamped(from, to, ease(x));

                if (x < 1f)
                    return;

                gui.Opacity = to;

                gui.whenTick -= handler;
                fading.Remove(gui);

                onComplete?.Invoke();
            };

            item.whenTick += handler;
            fading[item] = handler;
        }

        public static void FadeIn(
            this GUIItem item,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            item.Opacity = 0f;
            item.FadeTo(1f, duration, delay, ease, onComplete);
        }

        public static void FadeOut(
            this GUIItem item,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            item?.FadeTo(0f, duration, delay, ease, onComplete);
        }

        // RenderScale은 DrawRect가 중심 기준으로 적용하므로 자리를 안 옮기고 크기만 바뀐다.
        // 카드가 제자리에서 커지고 뒤집히는 연출이 좌표 계산 없이 값 하나로 끝난다
        public static void ScaleTo(
            this GUIItem item,
            Vector2 to,
            float duration,
            float delay = 0f,
            Func<float, float> ease = null,
            Action onComplete = null)
        {
            if (item == null) return;

            KillScale(item);

            ease ??= TweenHelper.EaseOutSine;

            Vector2 from = item.RenderScale;
            float time = 0f;
            Action<GUIItem, float> handler = null;

            handler = (gui, dt) =>
            {
                time += dt;
                float local = time - delay;

                if (local < 0f)
                    return;

                float x = duration > 0f
                    ? TweenHelper.XCalculator(local, 0f, duration)
                    : 1f;

                gui.RenderScale = Vector2.LerpUnclamped(from, to, ease(x));

                if (x < 1f)
                    return;

                gui.RenderScale = to;

                gui.whenTick -= handler;
                scaling.Remove(gui);

                onComplete?.Invoke();
            };

            item.whenTick += handler;
            scaling[item] = handler;
        }

        // 튀어나왔다 제자리로. EaseOutBack이 목표를 살짝 넘겼다 돌아오므로
        // 커졌다 작아지는 두 트윈을 이어붙이지 않아도 탄력이 붙는다
        public static void PunchScale(
            this GUIItem item,
            float amount = 0.25f,
            float duration = 0.25f,
            Action onComplete = null)
        {
            if (item == null) return;

            Vector2 baseScale = item.RenderScale;

            item.ScaleTo(baseScale * (1f + amount), duration * 0.35f, 0f, TweenHelper.EaseOutQuad,
                () => item.ScaleTo(baseScale, duration * 0.65f, 0f, TweenHelper.EaseOutBack, onComplete));
        }

        // 카드 뒤집기. 폭을 1 → 0 → 1로 돌리고, 폭이 0이 되는 순간 onHalf를 부른다.
        // 거기서 앞면/뒷면 텍스처를 바꾸면 옆으로 돌아가는 것처럼 보인다.
        //
        // |cos|을 쓰는 이유: 0과 1 근처에서 느리고 가운데서 빠르다.
        // 선형으로 하면 종이가 접히는 것처럼 보이고 회전으로 안 읽힌다
        public static void FlipTo(
            this GUIItem item,
            float duration,
            Action onHalf = null,
            Action onComplete = null)
        {
            if (item == null) return;

            KillScale(item);

            float height = item.RenderScale.y;
            float time = 0f;
            bool halfFired = false;
            Action<GUIItem, float> handler = null;

            handler = (gui, dt) =>
            {
                time += dt;

                float x = duration > 0f
                    ? TweenHelper.XCalculator(time, 0f, duration)
                    : 1f;

                gui.RenderScale = new Vector2(Mathf.Abs(Mathf.Cos(x * Mathf.PI)), height);

                // 폭이 0을 지나는 그 프레임에만. 여기서 안 바꾸면 뒤집히는 도중에
                // 그림이 튀어서 뒤집기가 아니라 깜빡임으로 보인다
                if (!halfFired && x >= 0.5f)
                {
                    halfFired = true;
                    onHalf?.Invoke();
                }

                if (x < 1f)
                    return;

                gui.RenderScale = new Vector2(1f, height);

                gui.whenTick -= handler;
                scaling.Remove(gui);

                onComplete?.Invoke();
            };

            item.whenTick += handler;
            scaling[item] = handler;
        }

        private static void KillScale(GUIItem item)
        {
            if (!scaling.TryGetValue(item, out Action<GUIItem, float> handler))
                return;

            item.whenTick -= handler;
            scaling.Remove(item);
        }

        private static void KillMove(GUIItem item)
        {
            if (!running.TryGetValue(item, out Action<GUIItem, float> handler))
                return;

            item.whenTick -= handler;
            running.Remove(item);
        }

        private static void KillFade(GUIItem item)
        {
            if (!fading.TryGetValue(item, out Action<GUIItem, float> handler))
                return;

            item.whenTick -= handler;
            fading.Remove(item);
        }

        public static void Kill(GUIItem item)
        {
            if (item == null) return;

            KillMove(item);
            KillFade(item);
            KillScale(item);
        }

        public static void Wave(
            IReadOnlyList<GUIItem> items,
            Vector2 offset,
            float duration,
            float step,
            Func<float, float> ease = null,
            bool inward = true,
            bool reverse = false,
            Action onAllComplete = null)
        {
            if (items == null || items.Count == 0)
            {
                onAllComplete?.Invoke();
                return;
            }

            int total = items.Count;
            int done = 0;

            Action tally = onAllComplete == null
                ? null
                : () =>
                {
                    done++;

                    if (done >= total)
                        onAllComplete();
                };

            for (int i = 0; i < total; i++)
            {
                GUIItem item = items[i];

                if (item == null)
                {
                    tally?.Invoke();
                    continue;
                }

                float delay = (reverse ? total - i - 1 : i) * step;

                if (inward)
                    item.MoveIn(offset, duration, delay, ease, tally);
                else
                    item.MoveOut(offset, duration, delay, ease, tally);
            }
        }
    }
}
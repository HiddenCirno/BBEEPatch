# -*- coding: utf-8 -*-
"""追加弹幕的【延迟】—— 手感调整：让剑气晚一点出，而不是和动作同时。

配置（[纹章解放] AttachBullets）：
    aD12:A1:0.2        触发弹幕:追加弹幕:延迟秒
    aD12:A1            省略延迟 = 0（立即）

实现：延迟项留在队列里每帧递减，减到 <=0 才生成。
"""
import io

P = "Modules/Es/EsEmblemBurst.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) Req 加 Delay
A = "        /// <summary>true = 我们自己推位移；false = 旋转 dir 交给动作自己飞。</summary>\n        public bool MoveMode;"
assert A in s
s = s.replace(A, A + "\n        /// <summary>追加弹幕的剩余延迟(秒)。>0 时留在队列里每帧递减。</summary>\n        public float Delay;", 1)

# ---------------------------------------------------------------- 2) 解析 触发:追加:延迟
B = """    private static string AttachFor(string startAction)
    {
        var raw = CfgAttach?.Value;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(startAction)) return null;
        foreach (var item in raw.Split('|'))
        {
            int c = item.IndexOf(':');
            if (c <= 0) continue;
            if (string.Equals(item.Substring(0, c).Trim(), startAction, StringComparison.OrdinalIgnoreCase))
            {
                var v = item.Substring(c + 1).Trim();
                if (v.Length > 0) return v;
            }
        }
        return null;
    }"""
assert B in s
B2 = """    /// <summary>查"这个弹幕要不要额外追加一个"。返回 追加action 与 延迟秒；没有则 false。</summary>
    private static bool AttachFor(string startAction, out string extra, out float delay)
    {
        extra = null; delay = 0f;
        var raw = CfgAttach?.Value;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(startAction)) return false;
        foreach (var item in raw.Split('|'))
        {
            int c = item.IndexOf(':');
            if (c <= 0) continue;
            if (!string.Equals(item.Substring(0, c).Trim(), startAction, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = item.Substring(c + 1).Trim();
            if (rest.Length == 0) return false;
            int c2 = rest.IndexOf(':');
            if (c2 > 0)
            {
                float d;
                if (float.TryParse(rest.Substring(c2 + 1).Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d)) delay = d;
                rest = rest.Substring(0, c2).Trim();
            }
            if (rest.Length == 0) return false;
            extra = rest;
            return true;
        }
        return false;
    }"""
s = s.replace(B, B2, 1)

# ---------------------------------------------------------------- 3) 入队处用新签名
C = """                    string extra = AttachFor(startAction);
                    if (extra != null)
                        _attach.Add(new Req
                        {
                            Mgr = __instance, Caster = caster, Idx = idx, Pos = pos,
                            DamageScale = damageScale, Action = extra,
                            Skill = skillActivate, Params = paramSet, Center = __result,
                            Dir = dir, MoveMode = true,
                        });"""
assert C in s
C2 = """                    string extra; float delay;
                    if (AttachFor(startAction, out extra, out delay))
                        _attach.Add(new Req
                        {
                            Mgr = __instance, Caster = caster, Idx = idx, Pos = pos,
                            DamageScale = damageScale, Action = extra,
                            Skill = skillActivate, Params = paramSet, Center = __result,
                            Dir = dir, MoveMode = true, Delay = delay,
                        });"""
s = s.replace(C, C2, 1)

# ---------------------------------------------------------------- 4) 帧末处理带延迟递减
D = """        if (_attach.Count > 0)
        {
            var ab = _attach.ToArray();
            _attach.Clear();
            _reentrant = true;
            try
            {
                foreach (var r in ab)
                {
                    if (r.Mgr == null) continue;
                    try
                    {"""
assert D in s
D2 = """        if (_attach.Count > 0)
        {
            _reentrant = true;
            try
            {
                // 从后往前：带延迟的项递减后留在队列里，到点才生成
                for (int ai = _attach.Count - 1; ai >= 0; ai--)
                {
                    var r = _attach[ai];
                    if (r.Delay > 0f) { r.Delay -= dtf; _attach[ai] = r; continue; }
                    _attach.RemoveAt(ai);
                    if (r.Mgr == null) continue;
                    try
                    {"""
s = s.replace(D, D2, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("延迟已加入")

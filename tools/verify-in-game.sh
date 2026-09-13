#!/usr/bin/env bash
# 在隔离环境中启动 Vintage Story 并运行 WyyPlayer 模组，用于游戏内验证。
#
# 存在的意义：本机 MVL 使用离线账号，直接运行 Vintagestory 会卡在登录检查、
# 根本走不到模组加载。必须经 VSRun（MVL 的启动封装，带 SessionManager 的
# Offline 补丁）启动。此外必须打开既有存档：--rndWorld 会停在新世界的
# 创建角色界面，而该界面会暂停客户端；客户端暂停时游戏 tick 不运行，
# 模组的 tick 驱动逻辑（含结束检测与超时兜底）就永远不会执行。
#
# 注意：本脚本依赖一个本机的启动封装（VSRun，见下方 VSRUN 变量）。它用于
# **开发者的自动化验证**，不是模组运行所必需 —— 普通玩家直接把模组放进
# Mods/ 启动游戏即可。没有该封装时请手动启动游戏并观察日志。
#
# 用法:
#   tools/verify-in-game.sh [等待秒数] [输出标签]
#
# 环境:
#   VS_VERIFY_SAVE   存档文件名（默认 elite.vcdbs）
#   VS_VERIFY_WORLD  世界名（默认 elite，即存档文件名去掉扩展名）
#   VS_VERIFY_ARGS   传给游戏的额外参数
#   VS_VERIFY_RECORD 设为 1 时同时抓取系统音频输出到 /tmp/<标签>.wav

set -uo pipefail

GAME_DIR="${VINTAGE_STORY:-/home/elite/.local/share/MVL/Release/1.22.7}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VSRUN="${VSRUN_DLL:-/home/elite/Projects/MVL/VSRun/bin/Release/net10.0/VSRun.dll}"
SAVE_SRC="/home/elite/.local/share/MVL/Modpack/VintagestoryData/Saves"
SAVE_NAME="${VS_VERIFY_SAVE:-elite.vcdbs}"
WORLD="${VS_VERIFY_WORLD:-${SAVE_NAME%.vcdbs}}"
# 默认复用已验证过的数据目录。全新目录实测会触发客户端暂停
# （暂停时游戏 tick 不运行，模组的 tick 驱动逻辑全部停摆），
# 且该暂停与窗口焦点无关。用 VS_VERIFY_DATA 可指定其他目录。
DATA="${VS_VERIFY_DATA:-/tmp/vsverify-data}"
LABEL="${2:-run}"
WAIT="${1:-120}"

MONITOR="$(pactl get-default-sink 2>/dev/null).monitor"

# 离线账号的 UID 必须与存档内玩家的 UID 一致，否则游戏会认为该玩家不存在，
# 弹出「创建角色」界面并暂停客户端 —— 而客户端暂停时游戏 tick 不运行，
# 模组的 tick 驱动逻辑（结束检测、超时兜底）全部停摆。
# 因此从 MVL 的 data.json 读取当前账号，而不是自造一个。
read -r PLAYER_NAME PLAYER_UID < <(python3 - <<'PY'
import json, pathlib
try:
    d = json.load(pathlib.Path("/home/elite/.local/share/MVL/data.json").open())
    cur = d.get("currentAccount")
    acct = next((a for a in d.get("account", []) if a.get("playerName") == cur), None)
    if acct:
        print(acct.get("playerName", "VERIFY"), acct.get("uid") or acct.get("playerName", "VERIFY"))
    else:
        print("VERIFY", "VERIFY")
except Exception:
    print("VERIFY", "VERIFY")
PY
)
[[ -z "${PLAYER_NAME:-}" ]] && PLAYER_NAME=VERIFY
[[ -z "${PLAYER_UID:-}" ]] && PLAYER_UID="$PLAYER_NAME"

cleanup() {
    [[ -n "${GAME_PID:-}" ]] && kill "$GAME_PID" 2>/dev/null
    [[ -n "${REC_PID:-}" ]] && kill "$REC_PID" 2>/dev/null
    sleep 2
    [[ -n "${GAME_PID:-}" ]] && kill -9 "$GAME_PID" 2>/dev/null
    [[ -n "${REC_PID:-}" ]] && kill -9 "$REC_PID" 2>/dev/null
    return 0
}
trap cleanup EXIT INT TERM

# --- 1. 构建 ---
echo "[1/5] 构建模组"
( cd "$REPO" && VINTAGE_STORY="$GAME_DIR" dotnet build -c Debug -v q --nologo ) || { echo "构建失败"; exit 1; }

# --- 2. 准备隔离数据目录（不动玩家存档）---
echo "[2/5] 准备隔离数据目录 $DATA"
mkdir -p "$DATA/Saves" "$DATA/Mods/vscloudmusic"
# 复用玩家的客户端设置，但强制窗口化，避免全屏占用桌面
python3 - "$DATA" <<'PY'
import json, sys, pathlib
src = pathlib.Path("/home/elite/.local/share/MVL/Modpack/VintagestoryData/clientsettings.json")
dst = pathlib.Path(sys.argv[1]) / "clientsettings.json"
if src.exists() and not dst.exists():
    d = json.load(src.open())
    d.setdefault("intSettings", {}).update({"gameWindowMode": 0, "screenWidth": 1024, "screenHeight": 640})
    d.setdefault("boolSettings", {}).update({
        "pauseGameOnLostFocus": False,
        "showCreativeHelpDialog": False,
        "showSurvivalHelpDialog": False,
        "immersiveMouseMode": False,
    })
    d.setdefault("stringListSettings", {})["disabledMods"] = []
    json.dump(d, dst.open("w"), ensure_ascii=False)
    print("      clientsettings 已生成（窗口化 1024x640）")
PY
# 复制存档副本 —— 玩家原档始终不被改动
if [[ -f "$SAVE_SRC/$SAVE_NAME" && ! -f "$DATA/Saves/$SAVE_NAME" ]]; then
    cp "$SAVE_SRC/$SAVE_NAME" "$DATA/Saves/$SAVE_NAME"
    echo "      存档已复制: $SAVE_NAME"
fi
cp -r "$REPO/src/WyyPlayer/bin/Debug/Mods/mod/." "$DATA/Mods/vscloudmusic/"

# --- 3. 初始化数据目录结构（复刻 MVL 的 InitData 步骤）---
echo "[3/5] InitData"
VINTAGE_STORY_PATH="$GAME_DIR" \
RUN_CONFIG="{\"vintageStoryPath\":\"$GAME_DIR\",\"vintageStoryDataPath\":\"$DATA\",\"assemblyPath\":\"Vintagestory.dll\",\"executableType\":\"InitData\",\"useAnsiLogger\":true,\"account\":{\"playerName\":\"$PLAYER_NAME\",\"uid\":\"$PLAYER_UID\",\"offline\":true}}" \
    dotnet "$VSRUN" >/dev/null 2>&1

# --- 4. 复制测试夹具 + 抓音频（可选）+ 启动游戏 ---
rm -f "$DATA/Logs/client-main.log"
# 把仓库的测试 mp3 复制到模组数据目录：<数据目录>/vscloudmusic/test.mp3
# （模组用 api.GetOrCreateDataPath("vscloudmusic") 定位，等价于 GamePaths.DataPath + "vscloudmusic"）
mkdir -p "$DATA/vscloudmusic"
[ -f "$DATA/vscloudmusic/credentials.txt" ] && echo "      检测到凭据文件，将播放真实歌曲" || echo "      无凭据（模组会记日志并放弃开播）"
if [[ "${VS_VERIFY_RECORD:-0}" == "1" ]]; then
    echo "[4/5] 开始抓取系统音频 ($MONITOR)"
    parec -d "$MONITOR" --file-format=wav "/tmp/${LABEL}.wav" >/dev/null 2>&1 &
    REC_PID=$!
    sleep 2
else
    echo "[4/5] 启动游戏（未开启音频抓取）"
fi

VINTAGE_STORY_PATH="$GAME_DIR" \
RUN_CONFIG="{\"vintageStoryPath\":\"$GAME_DIR\",\"vintageStoryDataPath\":\"$DATA\",\"assemblyPath\":\"Vintagestory.dll\",\"executableType\":\"StartGame\",\"useAnsiLogger\":true,\"account\":{\"playerName\":\"$PLAYER_NAME\",\"uid\":\"$PLAYER_UID\",\"offline\":true}}" \
    dotnet "$VSRUN" --openWorld "$WORLD" ${VS_VERIFY_ARGS:-} >/tmp/vsverify.out 2>&1 &
GAME_PID=$!
echo "      游戏 pid=$GAME_PID, 世界=$WORLD, 离线账号=$PLAYER_NAME/$PLAYER_UID"

# 主动聚焦游戏窗口：客户端失焦时会暂停，而暂停时游戏 tick 不运行，
# 模组的 tick 驱动逻辑（结束检测、超时兜底）就永远不会执行。
# 结合 clientsettings 里的 pauseGameOnLostFocus=false，双保险。
if command -v niri >/dev/null 2>&1; then
    for _ in $(seq 1 20); do
        sleep 3
        WID=$(timeout 6 niri msg windows 2>/dev/null \
              | grep -B4 -iE "vintage|Vintagestory" \
              | grep -oE "Window ID [0-9]+" | head -1 | grep -oE "[0-9]+")
        if [[ -n "$WID" ]]; then
            timeout 6 niri msg action focus-window --id "$WID" >/dev/null 2>&1 \
                && echo "      已聚焦游戏窗口 (id=$WID)"
            break
        fi
    done
fi

# --- 5. 轮询日志 + 采样 fd 与内存 ---
echo "[5/5] 轮询日志（最多 ${WAIT}s）；每 30s 采样一次 fd 数与 RSS"
SAMPLES=()
for ((i = 0; i < WAIT / 5; i++)); do
    sleep 5
    if (( i % 6 == 5 )); then
        if [[ -d "/proc/$GAME_PID" ]]; then
            FDS=$(ls /proc/"$GAME_PID"/fd 2>/dev/null | wc -l)
            RSS_KB=$(awk '/VmRSS/{print $2}' /proc/"$GAME_PID"/status 2>/dev/null)
            line="$(( (i+1)*5 ))s  fd=$FDS  rss=$(( RSS_KB / 1024 ))MB"
            echo "      $line"
            SAMPLES+=("$line")
        fi
    fi
    # 早期退出是可选的：模组已从「循环单个测试夹具」变成真正的播放器，
    # 切换歌曲本身就会释放并重建播放硬件，因此「释放日志」不再是会话终点的信号。
    # 默认跑满指定时长，由使用者自己中断。
    if [[ "${VS_VERIFY_EXIT_ON_RELEASE:-0}" == "1" ]] && [[ -f "$DATA/Logs/client-main.log" ]] \
        && grep -aq "释放解码器" "$DATA/Logs/client-main.log"; then
        echo "      ✓ 已观测到释放日志（约 $((i * 5))s）"
        break
    fi
done
echo
echo "════════ fd / 内存采样（fd 应保持平稳，不应线性增长）════════"
printf '  %s\n' "${SAMPLES[@]:-无采样}"

echo
echo "════════ vscloudmusic 日志 ════════"
grep -a "vscloudmusic" "$DATA/Logs/client-main.log" 2>/dev/null | grep -av "systems on Client" || echo "  (无)"
echo "════════ 客户端暂停次数 ════════"
printf "  %s\n" "$(grep -ac 'pause state' "$DATA/Logs/client-main.log" 2>/dev/null || echo 0)"
echo "════════ 模组异常 ════════"
grep -aiE "exception|vscloudmusic.*error" "$DATA/Logs/client-main.log" 2>/dev/null | head -5 || echo "  (无)"
echo
echo "日志: $DATA/Logs/client-main.log"
[[ -f "/tmp/${LABEL}.wav" ]] && echo "录音: /tmp/${LABEL}.wav"

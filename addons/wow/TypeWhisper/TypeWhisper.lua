-- TypeWhisper add-on for World of Warcraft.
--
-- TypeWhisper cannot talk to the game; it can only press keys. This add-on gives four of them a meaning.
--
-- Ctrl+Shift+F11 opens the chat. It is bound to the game's own "Open Chat" command: a closed chat opens, and while
-- the chat is open the chord goes into the chat input, which ignores it, so the text and focus stay as they are.
-- Pressing Enter instead would send or close an open chat and lose the dictation.
--
-- Ctrl+Shift+F8, F9 and F10 mean recording, transcribing and stopped. They drive a cast bar in the game's own style.
-- No audio reaches the game, so the waveform is drawn, not measured. A finished dictation needs no key: the bar
-- completes when the chat opens for the text, or when the text arrives in a chat that was already open.
--
-- A focused chat input takes every key, so key bindings do not fire while it is open. The add-on then listens on the
-- chat input for its three chords only. It never changes the chat: the game's own binding opens it, and sending stays
-- with Blizzard's code, so it works during encounters too.

local OPEN_CHAT_KEY = "CTRL-SHIFT-F11"
local SIGNALS = {
    { key = "CTRL-SHIFT-F8", button = "TypeWhisperRecordingButton", signal = "recording" },
    { key = "CTRL-SHIFT-F9", button = "TypeWhisperTranscribingButton", signal = "transcribing" },
    { key = "CTRL-SHIFT-F10", button = "TypeWhisperStoppedButton", signal = "stopped" },
}
-- The same chords as the chat input reports them: the key, with Ctrl and Shift held.
local CHAT_SIGNALS = { F8 = "recording", F9 = "transcribing", F10 = "stopped" }
local MODIFIER_KEYS = { LCTRL = "ctrl", RCTRL = "ctrl", LSHIFT = "shift", RSHIFT = "shift" }
-- Languages TypeWhisper can translate chat messages into. The saved value is the English name it gives the LLM.
local LANGUAGES = { "English", "German", "French", "Spanish", "Italian", "Portuguese", "Russian", "Chinese", "Korean" }
local LOG_LINES = 40
-- Offsets are kept in screen units, so the bar stays in place when its size changes.
local DEFAULT_POINT = { "CENTER", "CENTER", 0, -180 }
local MIN_SCALE, MAX_SCALE, SCALE_STEP = 0.5, 2, 0.1
local RECORDING_TIMEOUT, TRANSCRIBING_TIMEOUT = 300, 30
local WAVE_BARS = 36

-- Texts --------------------------------------------------------------------------------------------------------------

local L = {
    dictating = "Dictating",
    transcribing = "Transcribing…",
    done = "Done",
    move = "Drag to move · /tw move to lock",
    ready = "Ready. Dictate while the game is in front; the chat opens by itself.",
    notReady = "Not ready yet. Leave combat or type /reload.",
    help = "/tw opens these settings, /tw test shows the bar, /tw move places it, /tw scale 120 sizes it, /tw reset puts it back.",
    size = "Bar size: %d%%",
    locked = "Bar locked.",
    reset = "Bar position reset.",
    about = "Dictate into the chat with TypeWhisper. The chat opens by itself, and a cast bar shows what TypeWhisper is doing.",
    keys = "Ctrl+Shift+F11 opens the chat. Ctrl+Shift+F8, F9 and F10 drive the cast bar. TypeWhisper presses them for you.",
    testBar = "Test bar",
    moveBar = "Move bar",
    lockBar = "Lock bar",
    resetBar = "Reset position",
    combat = "Leave combat to open the settings.",
    send = "Send messages automatically",
    showBar = "Show the cast bar while you dictate",
    translate = "Translate my messages into",
    off = "Off",
    languages = { English = "English", German = "German", French = "French", Spanish = "Spanish", Italian = "Italian",
        Portuguese = "Portuguese", Russian = "Russian", Chinese = "Chinese", Korean = "Korean" },
    translateNote = "TypeWhisper translates with the default LLM of its workflows. Dictate once and type /reload to see which one.",
    translateWith = "TypeWhisper translates with %s, its default LLM for workflows.",
    noLlm = "TypeWhisper has no LLM ready for translation (%s). Choose Default LLM under Workflows in TypeWhisper; until then, messages go out untranslated.",
    noLlmNone = "TypeWhisper has no default LLM for translation. Choose one under Workflows in TypeWhisper; until then, messages go out untranslated.",
    needsLlm = "Translation needs a default LLM under Workflows in TypeWhisper.",
    applyNote = "TypeWhisper reads these settings when you type /reload or log out.",
    applyPending = "Changed. Type /reload so TypeWhisper uses the new settings.",
    reloadNow = "Reload now",
    replyKey = "To answer a whisper, press %s (Reply), then dictate.",
    replyNoKey = "To answer a whisper, type /r and a space, then dictate.",
}
local locale = GetLocale()
if locale == "deDE" then
    L.dictating = "Diktieren"
    L.transcribing = "Transkribieren…"
    L.done = "Fertig"
    L.move = "Zum Verschieben ziehen · /tw move sperrt"
    L.ready = "Bereit. Diktiere, während das Spiel vorne ist; der Chat öffnet sich von selbst."
    L.notReady = "Noch nicht bereit. Verlasse den Kampf oder gib /reload ein."
    L.help = "/tw öffnet diese Einstellungen, /tw test zeigt den Balken, /tw move verschiebt ihn, /tw scale 120 ändert die Größe, /tw reset setzt ihn zurück."
    L.size = "Balkengröße: %d %%"
    L.locked = "Balken gesperrt."
    L.reset = "Balken zurückgesetzt."
    L.about = "Diktiere mit TypeWhisper in den Chat. Der Chat öffnet sich von selbst, und ein Zauberbalken zeigt, was TypeWhisper gerade macht."
    L.keys = "Strg+Umschalt+F11 öffnet den Chat. Strg+Umschalt+F8, F9 und F10 steuern den Zauberbalken. TypeWhisper drückt sie für dich."
    L.testBar = "Balken testen"
    L.moveBar = "Balken verschieben"
    L.lockBar = "Balken sperren"
    L.resetBar = "Position zurücksetzen"
    L.combat = "Verlasse den Kampf, um die Einstellungen zu öffnen."
    L.send = "Nachrichten automatisch senden"
    L.showBar = "Zauberbalken beim Diktieren zeigen"
    L.translate = "Meine Nachrichten übersetzen in"
    L.off = "Aus"
    L.languages = { English = "Englisch", German = "Deutsch", French = "Französisch", Spanish = "Spanisch", Italian = "Italienisch",
        Portuguese = "Portugiesisch", Russian = "Russisch", Chinese = "Chinesisch", Korean = "Koreanisch" }
    L.translateNote = "TypeWhisper übersetzt mit dem Standard-LLM seiner Workflows. Diktiere einmal und gib /reload ein, dann steht hier, welches."
    L.translateWith = "TypeWhisper übersetzt mit %s, seinem Standard-LLM für Workflows."
    L.noLlm = "TypeWhisper hat kein LLM für die Übersetzung bereit (%s). Wähle in TypeWhisper unter Workflows ein Standard-LLM; bis dahin gehen Nachrichten unübersetzt raus."
    L.noLlmNone = "TypeWhisper hat kein Standard-LLM für die Übersetzung. Wähle in TypeWhisper unter Workflows eines; bis dahin gehen Nachrichten unübersetzt raus."
    L.needsLlm = "Für Übersetzungen braucht TypeWhisper ein Standard-LLM unter Workflows."
    L.applyNote = "TypeWhisper liest diese Einstellungen, wenn du /reload eingibst oder dich ausloggst."
    L.applyPending = "Geändert. Gib /reload ein, damit TypeWhisper die neuen Einstellungen nutzt."
    L.reloadNow = "Jetzt neu laden"
    L.replyKey = "Um auf ein Flüstern zu antworten, drück %s (Antworten) und diktiere dann."
    L.replyNoKey = "Um auf ein Flüstern zu antworten, gib /r und ein Leerzeichen ein und diktiere dann."
elseif locale == "zhCN" then
    L.dictating = "听写中"
    L.transcribing = "正在转写…"
    L.done = "完成"
    L.move = "拖动以移动 · /tw move 锁定"
    L.ready = "已就绪。游戏在前台时即可听写，聊天框会自动打开。"
    L.notReady = "尚未就绪。请脱离战斗或输入 /reload。"
    L.help = "/tw 打开此设置，/tw test 显示施法条，/tw move 调整位置，/tw scale 120 调整大小，/tw reset 恢复默认位置。"
    L.size = "施法条大小：%d%%"
    L.locked = "施法条已锁定。"
    L.reset = "施法条位置已重置。"
    L.about = "使用 TypeWhisper 在聊天中听写。聊天框会自动打开，施法条会显示 TypeWhisper 当前的状态。"
    L.keys = "Ctrl+Shift+F11 打开聊天框。Ctrl+Shift+F8、F9 和 F10 控制施法条，由 TypeWhisper 自动按下。"
    L.testBar = "测试施法条"
    L.moveBar = "移动施法条"
    L.lockBar = "锁定施法条"
    L.resetBar = "重置位置"
    L.combat = "请脱离战斗后再打开设置。"
    L.send = "自动发送消息"
    L.showBar = "听写时显示施法条"
    L.translate = "将我的消息翻译为"
    L.off = "关闭"
    L.languages = { English = "英语", German = "德语", French = "法语", Spanish = "西班牙语", Italian = "意大利语",
        Portuguese = "葡萄牙语", Russian = "俄语", Chinese = "中文", Korean = "韩语" }
    L.translateNote = "TypeWhisper 使用其工作流的默认 LLM 进行翻译。听写一次并输入 /reload 后，这里会显示具体是哪一个。"
    L.translateWith = "TypeWhisper 使用 %s（其工作流默认 LLM）进行翻译。"
    L.noLlm = "TypeWhisper 没有可用于翻译的 LLM（%s）。请在 TypeWhisper 的“工作流”中选择默认 LLM；在此之前，消息将以原文发送。"
    L.noLlmNone = "TypeWhisper 没有用于翻译的默认 LLM。请在 TypeWhisper 的“工作流”中选择一个；在此之前，消息将以原文发送。"
    L.needsLlm = "翻译需要在 TypeWhisper 的“工作流”中设置默认 LLM。"
    L.applyNote = "当你输入 /reload 或退出登录时，TypeWhisper 会读取这些设置。"
    L.applyPending = "已更改。请输入 /reload，让 TypeWhisper 使用新设置。"
    L.reloadNow = "立即重载"
    L.replyKey = "要回复密语，请按 %s（回复），然后开始听写。"
    L.replyNoKey = "要回复密语，请输入 /r 和一个空格，然后开始听写。"
elseif locale == "zhTW" then
    L.dictating = "聽寫中"
    L.transcribing = "正在轉寫…"
    L.done = "完成"
    L.move = "拖曳以移動 · /tw move 鎖定"
    L.ready = "已就緒。遊戲在前景時即可聽寫，聊天框會自動開啟。"
    L.notReady = "尚未就緒。請脫離戰鬥或輸入 /reload。"
    L.help = "/tw 開啟此設定，/tw test 顯示施法條，/tw move 調整位置，/tw scale 120 調整大小，/tw reset 恢復預設位置。"
    L.size = "施法條大小：%d%%"
    L.locked = "施法條已鎖定。"
    L.reset = "施法條位置已重設。"
    L.about = "使用 TypeWhisper 在聊天中聽寫。聊天框會自動開啟，施法條會顯示 TypeWhisper 目前的狀態。"
    L.keys = "Ctrl+Shift+F11 開啟聊天框。Ctrl+Shift+F8、F9 和 F10 控制施法條，由 TypeWhisper 自動按下。"
    L.testBar = "測試施法條"
    L.moveBar = "移動施法條"
    L.lockBar = "鎖定施法條"
    L.resetBar = "重設位置"
    L.combat = "請脫離戰鬥後再開啟設定。"
    L.send = "自動傳送訊息"
    L.showBar = "聽寫時顯示施法條"
    L.translate = "將我的訊息翻譯為"
    L.off = "關閉"
    L.languages = { English = "英語", German = "德語", French = "法語", Spanish = "西班牙語", Italian = "義大利語",
        Portuguese = "葡萄牙語", Russian = "俄語", Chinese = "中文", Korean = "韓語" }
    L.translateNote = "TypeWhisper 使用其工作流程的預設 LLM 進行翻譯。聽寫一次並輸入 /reload 後，這裡會顯示是哪一個。"
    L.translateWith = "TypeWhisper 使用 %s（其工作流程預設 LLM）進行翻譯。"
    L.noLlm = "TypeWhisper 沒有可用於翻譯的 LLM（%s）。請在 TypeWhisper 的「工作流程」中選擇預設 LLM；在此之前，訊息將以原文傳送。"
    L.noLlmNone = "TypeWhisper 沒有用於翻譯的預設 LLM。請在 TypeWhisper 的「工作流程」中選擇一個；在此之前，訊息將以原文傳送。"
    L.needsLlm = "翻譯需要在 TypeWhisper 的「工作流程」中設定預設 LLM。"
    L.applyNote = "當你輸入 /reload 或登出時，TypeWhisper 會讀取這些設定。"
    L.applyPending = "已變更。請輸入 /reload，讓 TypeWhisper 使用新設定。"
    L.reloadNow = "立即重新載入"
    L.replyKey = "要回覆密語，請按 %s（回覆），然後開始聽寫。"
    L.replyNoKey = "要回覆密語，請輸入 /r 和一個空格，然後開始聽寫。"
end
-- The game's own word for a broken cast, in the player's language.
L.stopped = _G.INTERRUPTED or "Interrupted"

local function Print(text)
    print("|cff3ba7ffTypeWhisper|r: " .. text)
end

-- The last signals and signal keys of this session, saved with the bar position for troubleshooting. Typed
-- text never reaches it: only the three signal keys and the add-on's own state changes are written.
local function Log(text)
    local log = TypeWhisperDB and TypeWhisperDB.log
    if not log then return end
    log[#log + 1] = format("%.2f %s", GetTime(), text)
    while #log > LOG_LINES do table.remove(log, 1) end
end

-- Cast bar -----------------------------------------------------------------------------------------------------------

-- Retail and Forever draw cast bars from atlases; Classic Era still uses the original textures.
local modern = C_Texture ~= nil and C_Texture.GetAtlasInfo ~= nil and C_Texture.GetAtlasInfo("ui-castingbar-frame") ~= nil
local WIDTH, HEIGHT = modern and 220 or 195, 13

local bar = CreateFrame("StatusBar", "TypeWhisperCastBar", UIParent)
bar:SetSize(WIDTH, HEIGHT)
-- Above the Options window, so the bar can be tested and moved from its settings page.
bar:SetFrameStrata("DIALOG")
bar:SetMinMaxValues(0, 1)
bar:SetValue(0)
bar:SetClampedToScreen(true)
bar:SetMovable(true)
bar:EnableMouse(false)
bar:RegisterForDrag("LeftButton")
bar:Hide()

local function Texture(layer, subLevel)
    return bar:CreateTexture(nil, layer, nil, subLevel)
end

if modern then
    local textBorder = Texture("BACKGROUND", 0)
    textBorder:SetAtlas("ui-castingbar-textbox")
    textBorder:SetPoint("TOPLEFT")
    textBorder:SetPoint("BOTTOMRIGHT", 0, -12)
    local background = Texture("BACKGROUND", 2)
    background:SetAtlas("ui-castingbar-background")
    background:SetPoint("TOPLEFT", -1, 1)
    background:SetPoint("BOTTOMRIGHT", 1, -1)
    local border = Texture("ARTWORK", 4)
    border:SetAtlas("ui-castingbar-frame")
    border:SetPoint("TOPLEFT", -2, 2)
    border:SetPoint("BOTTOMRIGHT", 2, -2)
    bar.Flash = Texture("OVERLAY", 1)
    bar.Flash:SetAtlas("ui-castingbar-full-glow-standard")
    bar.Flash:SetPoint("TOPLEFT", -1, 1)
    bar.Flash:SetPoint("BOTTOMRIGHT", 1, -1)
    bar.Spark = Texture("OVERLAY", 2)
    bar.Spark:SetAtlas("ui-castingbar-pip")
    bar.Spark:SetSize(8, HEIGHT + 9)
    -- Like the player cast bar: the text sits in the box under the bar.
    bar.Text = bar:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    bar.Text:SetPoint("TOP", bar, "BOTTOM", 0, 1)
    bar.Time = bar:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    bar.Time:SetPoint("TOPRIGHT", bar, "BOTTOMRIGHT", -6, 1)
else
    local background = Texture("BACKGROUND", 0)
    background:SetAllPoints()
    background:SetColorTexture(0, 0, 0, 0.5)
    local border = Texture("ARTWORK", 1)
    border:SetTexture("Interface\\CastingBar\\UI-CastingBar-Border")
    border:SetSize(256, 64)
    border:SetPoint("TOP", 0, 28)
    bar.Flash = Texture("OVERLAY", 1)
    bar.Flash:SetTexture("Interface\\CastingBar\\UI-CastingBar-Flash")
    bar.Flash:SetSize(256, 64)
    bar.Flash:SetPoint("TOP", 0, 28)
    bar.Flash:SetVertexColor(0, 1, 0)
    bar.Spark = Texture("OVERLAY", 2)
    bar.Spark:SetTexture("Interface\\CastingBar\\UI-CastingBar-Spark")
    bar.Spark:SetSize(32, 32)
    bar.Text = bar:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    bar.Text:SetSize(185, 16)
    bar.Text:SetPoint("TOP", 0, 5)
    bar.Time = bar:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    bar.Time:SetPoint("RIGHT", -4, 1)
end
bar.Flash:SetBlendMode("ADD")
bar.Spark:SetBlendMode("ADD")

-- The waveform: thin columns of the channel fill, green like a channeled spell.
bar.Wave = {}
local slot = (WIDTH - 4) / WAVE_BARS
for i = 1, WAVE_BARS do
    local wave = Texture("ARTWORK", modern and 2 or 0)
    if modern then
        wave:SetAtlas("ui-castingbar-filling-channel")
    else
        wave:SetTexture("Interface\\TargetingFrame\\UI-StatusBar")
        wave:SetVertexColor(0, 1, 0)
    end
    wave:SetSize(math.max(2, slot * 0.55), 2)
    wave:SetPoint("CENTER", bar, "LEFT", 2 + (i - 0.5) * slot, 0)
    bar.Wave[i] = wave
end

local FILLS = {
    transcribing = { atlas = "ui-castingbar-filling-standard", color = { 1, 0.7, 0 } },
    done = { atlas = "ui-castingbar-full-standard", color = { 0, 1, 0 } },
    stopped = { atlas = "ui-castingbar-interrupted", color = { 1, 0, 0 } },
}

local function SetFill(kind)
    local fill = FILLS[kind]
    if modern then
        bar:SetStatusBarTexture(fill.atlas)
        bar:SetStatusBarColor(1, 1, 1)
    else
        bar:SetStatusBarTexture("Interface\\TargetingFrame\\UI-StatusBar")
        bar:SetStatusBarColor(fill.color[1], fill.color[2], fill.color[3])
    end
end
SetFill("transcribing")

-- State --------------------------------------------------------------------------------------------------------------

local state, stateStart, fadeStart = nil, 0, nil
local levels, phase, sampleClock = {}, 0, 0
-- The chat input that had focus when transcribing began, and whether its text changed since.
local chatAtStart, chatTyped = nil, false

local function FocusedChat()
    local focus = GetCurrentKeyBoardFocus and GetCurrentKeyBoardFocus()
    local name = focus and focus.GetName and focus:GetName()
    if type(name) == "string" and name:find("^ChatFrame%d+EditBox$") then return focus end
    return nil
end

local function ShowWave(show)
    for _, wave in ipairs(bar.Wave) do wave:SetShown(show) end
end

local function SetState(newState)
    state, stateStart, fadeStart = newState, GetTime(), nil
    chatAtStart, chatTyped = FocusedChat(), false
    bar:SetAlpha(1)
    bar.Flash:SetAlpha(0)
    bar.Spark:Hide()
    bar.Time:SetText("")
    ShowWave(newState == "recording" or newState == "move")
    if newState == "recording" or newState == "move" then
        for i = 1, WAVE_BARS do levels[i] = 0 end
        bar:SetValue(0)
        bar.Text:SetText(newState == "move" and L.move or L.dictating)
    elseif newState == "transcribing" then
        SetFill("transcribing")
        bar:SetValue(0)
        bar.Spark:Show()
        bar.Text:SetText(L.transcribing)
    elseif newState == "done" then
        SetFill("done")
        bar:SetValue(1)
        bar.Flash:SetAlpha(1)
        bar.Text:SetText(L.done)
    elseif newState == "stopped" then
        SetFill("stopped")
        bar:SetValue(1)
        bar.Text:SetText(L.stopped)
    end
    bar:Show()
end

local function FadeOut()
    fadeStart = fadeStart or GetTime()
end

-- Draws a voice-like level, scrolling from right to left like TypeWhisper's own waveform: syllables inside phrases,
-- quick to rise and slow to fall.
local function Animate(elapsed)
    sampleClock = sampleClock + elapsed
    if sampleClock < 1 / 30 then return end
    sampleClock = 0
    phase = phase + 0.38
    local syllable = (math.sin(phase) + 1) * 0.5
    local phrase = (math.sin(phase * 0.16) + 1) * 0.5
    local target = math.min(0.94, math.max(0.04, (syllable * 0.56 + math.random() * 0.3) * (0.3 + 0.7 * phrase)))
    for i = 1, WAVE_BARS - 1 do levels[i] = levels[i + 1] end
    local previous = levels[WAVE_BARS - 1]
    levels[WAVE_BARS] = previous + (target - previous) * (target > previous and 0.68 or 0.2)
    for i, wave in ipairs(bar.Wave) do
        local envelope = math.sin((i - 0.5) / WAVE_BARS * math.pi)
        wave:SetHeight(math.max(2, levels[i] * envelope * (HEIGHT - 3)))
        wave:SetAlpha(0.55 + 0.45 * envelope)
    end
end

bar:SetScript("OnUpdate", function(self, elapsed)
    local now = GetTime()
    local age = now - stateStart
    if fadeStart then
        local alpha = 1 - (now - fadeStart) / 0.4
        if alpha <= 0 then
            state, fadeStart = nil, nil
            self:Hide()
            return
        end
        self:SetAlpha(alpha)
    end
    if state == "recording" or state == "move" then
        Animate(elapsed)
        if state == "recording" then
            self.Time:SetText(format("%d:%02d", math.floor(age / 60), math.floor(age % 60)))
            if age > RECORDING_TIMEOUT then FadeOut() end
        end
    elseif state == "transcribing" then
        local value = 0.95 * (1 - math.exp(-age / 1.2))
        self:SetValue(value)
        self.Spark:SetPoint("CENTER", self, "LEFT", value * WIDTH, modern and 0 or 2)
        -- TypeWhisper opens the chat, or pastes into the chat that was open: the cast is complete.
        local chat = FocusedChat()
        if chat ~= chatAtStart or (chat ~= nil and chatTyped) then
            SetState("done")
        elseif age > TRANSCRIBING_TIMEOUT then
            FadeOut()
        end
    elseif state == "done" then
        self.Flash:SetAlpha(math.max(0, 1 - age / 0.3))
        if age > 0.6 then FadeOut() end
    elseif state == "stopped" then
        if age > 1.2 then FadeOut() end
    end
end)

local function Signal(signal, source)
    Log(format("signal %s via %s in state %s", signal, tostring(source), tostring(state)))
    if state == "move" then return end
    -- With the bar off, TypeWhisper shows its own indicator once it reads the setting; the test always plays.
    if source ~= "test" and TypeWhisperDB and TypeWhisperDB.showBar == false then return end
    -- One chord can reach both the binding and the chat input, for example when the chat opens on that key.
    if signal == state and GetTime() - stateStart < 0.3 then return end
    if signal == "recording" or signal == "transcribing" then
        SetState(signal)
    elseif signal == "stopped" and (state == "recording" or state == "transcribing") then
        SetState("stopped")
    end
end

-- Position -----------------------------------------------------------------------------------------------------------

local function Scale()
    return TypeWhisperDB and TypeWhisperDB.scale or 1
end

local function Place()
    local point = TypeWhisperDB and TypeWhisperDB.point or DEFAULT_POINT
    local scale = Scale()
    bar:SetScale(scale)
    bar:ClearAllPoints()
    bar:SetPoint(point[1], UIParent, point[2], point[3] / scale, point[4] / scale)
end

bar:SetScript("OnDragStart", function(self) self:StartMoving() end)
bar:SetScript("OnDragStop", function(self)
    self:StopMovingOrSizing()
    -- The add-on saves the position itself, so the game's layout cache does not keep a second copy.
    self:SetUserPlaced(false)
    local point, _, relativePoint, x, y = self:GetPoint()
    local scale = Scale()
    TypeWhisperDB.point = { point, relativePoint, x * scale, y * scale }
end)

-- Bindings -----------------------------------------------------------------------------------------------------------

local owner = CreateFrame("Frame", "TypeWhisperBindingOwner")

for _, entry in ipairs(SIGNALS) do
    -- Like the game's own key-to-button bindings: an unplaced button that reacts when the key goes down.
    local button = CreateFrame("Button", entry.button, UIParent)
    button:RegisterForClicks("AnyDown")
    button:SetScript("OnClick", function() Signal(entry.signal, "binding") end)
end

local function Bind()
    -- Override bindings sit on top of the player's own key bindings and are never saved with them.
    SetOverrideBinding(owner, true, OPEN_CHAT_KEY, "OPENCHAT")
    for _, entry in ipairs(SIGNALS) do
        SetOverrideBindingClick(owner, true, entry.key, entry.button, "LeftButton")
    end
end

-- While the chat input has focus it takes the chords, so it reports them here. Hooks only listen: the chat's own
-- handlers run first and unchanged, and nothing is written back.
local hookedChats, hookedCount = {}, 0
-- Ctrl and Shift as the chat input saw them. TypeWhisper presses and releases the whole chord at once, so by the
-- time the game handles F8 the live modifier state can already read as released.
local chatModifiers = { ctrl = false, shift = false }

local function OnChatKeyDown(_, key)
    local modifier = MODIFIER_KEYS[key]
    if modifier then
        chatModifiers[modifier] = true
        return
    end
    local signal = CHAT_SIGNALS[key]
    if not signal then return end
    local ctrl = chatModifiers.ctrl or IsControlKeyDown()
    local shift = chatModifiers.shift or IsShiftKeyDown()
    Log(format("chat key %s ctrl=%s shift=%s", key, tostring(ctrl), tostring(shift)))
    if ctrl and shift and not IsAltKeyDown() then Signal(signal, "chat") end
end

local function OnChatKeyUp(_, key)
    local modifier = MODIFIER_KEYS[key]
    if modifier then chatModifiers[modifier] = false end
end

local function OnChatFocusLost()
    -- Releases that happen elsewhere never reach this input.
    chatModifiers.ctrl, chatModifiers.shift = false, false
end

local function OnChatTextChanged()
    chatTyped = true
end

local function ListenOnChat(editBox)
    if not editBox or hookedChats[editBox] then return end
    hookedChats[editBox] = true
    hookedCount = hookedCount + 1
    editBox:HookScript("OnKeyDown", OnChatKeyDown)
    editBox:HookScript("OnKeyUp", OnChatKeyUp)
    editBox:HookScript("OnEditFocusLost", OnChatFocusLost)
    editBox:HookScript("OnTextChanged", OnChatTextChanged)
end

-- Whisper windows add chat inputs later; each is picked up the first time it has focus.
owner:SetScript("OnUpdate", function()
    local chat = FocusedChat()
    if chat and not hookedChats[chat] then
        ListenOnChat(chat)
        Log("listening on " .. chat:GetName())
    end
end)

local function Bound()
    if GetBindingAction(OPEN_CHAT_KEY, true) ~= "OPENCHAT" then return false end
    for _, entry in ipairs(SIGNALS) do
        if GetBindingAction(entry.key, true) ~= "CLICK " .. entry.button .. ":LeftButton" then return false end
    end
    return true
end

owner:RegisterEvent("PLAYER_LOGIN")
owner:SetScript("OnEvent", function(self, event)
    if event == "PLAYER_LOGIN" then
        TypeWhisperDB = TypeWhisperDB or {}
        TypeWhisperDB.log = {}
        Place()
        for i = 1, 50 do ListenOnChat(_G["ChatFrame" .. i .. "EditBox"]) end
        Log(format("login %s, listening on %d chat inputs", modern and "modern" or "classic", hookedCount))
    end
    -- Bindings cannot change in combat, for example after /reload during a fight.
    if InCombatLockdown() then
        self:RegisterEvent("PLAYER_REGEN_ENABLED")
        return
    end
    self:UnregisterEvent("PLAYER_REGEN_ENABLED")
    Bind()
    Log("bound " .. tostring(Bound()))
end)

-- Commands -----------------------------------------------------------------------------------------------------------

local function TestBar()
    if state == "move" then return end
    Signal("recording", "test")
    C_Timer.After(2.5, function() if state == "recording" then Signal("transcribing", "test") end end)
    C_Timer.After(3.7, function() if state == "transcribing" then SetState("done") end end)
end

local function ToggleMove()
    if state == "move" then
        bar:EnableMouse(false)
        state = nil
        bar:Hide()
        Print(L.locked)
    else
        bar:EnableMouse(true)
        SetState("move")
    end
end

local function ResetPosition()
    TypeWhisperDB.point = nil
    Place()
    Print(L.reset)
end

-- Sizes in steps of 10 %, then plays the test so the new size is visible.
local function SetBarScale(scale)
    scale = math.floor(math.max(MIN_SCALE, math.min(MAX_SCALE, scale)) / SCALE_STEP + 0.5) * SCALE_STEP
    TypeWhisperDB.scale = scale
    Place()
    if not state then TestBar() end
end

-- Settings -----------------------------------------------------------------------------------------------------------

local GetMetadata = C_AddOns and C_AddOns.GetAddOnMetadata or GetAddOnMetadata
local panel = CreateFrame("Frame", "TypeWhisperSettingsPanel")
panel:Hide()

local logo = panel:CreateTexture(nil, "ARTWORK")
logo:SetTexture("Interface\\AddOns\\TypeWhisper\\Icon.tga")
logo:SetSize(40, 40)
logo:SetPoint("TOPLEFT", 16, -16)
local title = panel:CreateFontString(nil, "ARTWORK", "GameFontNormalLarge")
title:SetPoint("TOPLEFT", logo, "TOPRIGHT", 10, -3)
title:SetText("TypeWhisper")
local version = panel:CreateFontString(nil, "ARTWORK", "GameFontDisableSmall")
version:SetPoint("TOPLEFT", title, "BOTTOMLEFT", 0, -4)
version:SetText("Version " .. (GetMetadata and GetMetadata("TypeWhisper", "Version") or ""))

local function Paragraph(font, anchor, gap, text)
    local line = panel:CreateFontString(nil, "ARTWORK", font)
    line:SetPoint("TOPLEFT", anchor, "BOTTOMLEFT", 0, -gap)
    line:SetPoint("RIGHT", panel, "RIGHT", -16, 0)
    line:SetJustifyH("LEFT")
    if text then line:SetText(text) end
    return line
end
local about = Paragraph("GameFontHighlight", logo, 16, L.about)
panel.Status = Paragraph("GameFontNormal", about, 12)

local function PanelButton(text, onClick)
    local button = CreateFrame("Button", nil, panel, "UIPanelButtonTemplate")
    button:SetSize(170, 24)
    button:SetText(text)
    button:SetScript("OnClick", onClick)
    return button
end

-- Dictation settings. TypeWhisper reads them from the saved variables, which the game writes on /reload or logout.
local changedThisSession = false
local RefreshPanel

local function Checkbox(anchor, gap, label, get, set)
    local check = CreateFrame("CheckButton", nil, panel, "UICheckButtonTemplate")
    check:SetSize(26, 26)
    check:SetPoint("TOPLEFT", anchor, "BOTTOMLEFT", 0, -gap)
    local text = panel:CreateFontString(nil, "ARTWORK", "GameFontHighlight")
    text:SetPoint("LEFT", check, "RIGHT", 4, 1)
    text:SetText(label)
    check:SetScript("OnClick", function(self)
        set(self:GetChecked() and true or false)
        changedThisSession = true
        RefreshPanel()
    end)
    check.Refresh = function() check:SetChecked(get()) end
    return check
end
panel.Send = Checkbox(panel.Status, 14, L.send,
    function() return TypeWhisperDB.send ~= false end,
    function(on) TypeWhisperDB.send = on end)
panel.ShowBar = Checkbox(panel.Send, 2, L.showBar,
    function() return TypeWhisperDB.showBar ~= false end,
    function(on) TypeWhisperDB.showBar = on end)

local translateLabel = panel:CreateFontString(nil, "ARTWORK", "GameFontHighlight")
translateLabel:SetPoint("TOPLEFT", panel.ShowBar, "BOTTOMLEFT", 4, -14)
translateLabel:SetText(L.translate)

local function SetTranslation(language)
    TypeWhisperDB.translateTo = language
    changedThisSession = true
    RefreshPanel()
end

-- A list of languages in the game's own dropdown, which every current client has.
local menuCreated, menu = pcall(CreateFrame, "DropdownButton", nil, panel, "WowStyle1DropdownTemplate")
if menuCreated and menu and menu.SetupMenu then
    menu:SetWidth(190)
    -- The game builds the menu right away, before it has loaded the saved variables.
    menu:SetupMenu(function(_, root)
        local function IsSelected(language)
            return (TypeWhisperDB and TypeWhisperDB.translateTo) == (language ~= "" and language or nil)
        end
        local function Select(language) SetTranslation(language ~= "" and language or nil) end
        root:CreateRadio(L.off, IsSelected, Select, "")
        for _, language in ipairs(LANGUAGES) do root:CreateRadio(L.languages[language], IsSelected, Select, language) end
    end)
    panel.Translate = menu
else
    -- Older clients without the dropdown: a button that steps through the languages, right click backwards.
    panel.Translate = PanelButton("", function(_, mouseButton)
        local index = 0
        for i, language in ipairs(LANGUAGES) do
            if language == TypeWhisperDB.translateTo then index = i end
        end
        SetTranslation(LANGUAGES[(index + (mouseButton == "RightButton" and -1 or 1)) % (#LANGUAGES + 1)])
    end)
    panel.Translate:RegisterForClicks("LeftButtonUp", "RightButtonUp")
end
panel.Translate:SetPoint("LEFT", translateLabel, "RIGHT", 10, 0)
panel.TranslateNote = Paragraph("GameFontDisableSmall", translateLabel, 12)

panel.Apply = Paragraph("GameFontHighlightSmall", panel.TranslateNote, 14)
panel.Reload = PanelButton(L.reloadNow, function() ReloadUI() end)
panel.Reload:SetPoint("TOPLEFT", panel.Apply, "BOTTOMLEFT", 0, -6)
panel.Reply = Paragraph("GameFontHighlight", panel.Reload, 16)

-- Cast bar tools.
local keys = Paragraph("GameFontHighlightSmall", panel.Reply, 18, L.keys)
panel.TestButton = PanelButton(L.testBar, TestBar)
panel.TestButton:SetPoint("TOPLEFT", keys, "BOTTOMLEFT", 0, -12)
panel.MoveButton = PanelButton(L.moveBar, function() ToggleMove(); RefreshPanel() end)
panel.MoveButton:SetPoint("LEFT", panel.TestButton, "RIGHT", 8, 0)
panel.ResetButton = PanelButton(L.resetBar, ResetPosition)
panel.ResetButton:SetPoint("LEFT", panel.MoveButton, "RIGHT", 8, 0)

panel.SizeText = panel:CreateFontString(nil, "ARTWORK", "GameFontHighlight")
panel.SizeText:SetPoint("TOPLEFT", panel.TestButton, "BOTTOMLEFT", 0, -18)
panel.Smaller = PanelButton("-", function() SetBarScale(Scale() - SCALE_STEP); RefreshPanel() end)
panel.Smaller:SetSize(32, 24)
panel.Smaller:SetPoint("LEFT", panel.SizeText, "RIGHT", 12, 0)
panel.Larger = PanelButton("+", function() SetBarScale(Scale() + SCALE_STEP); RefreshPanel() end)
panel.Larger:SetSize(32, 24)
panel.Larger:SetPoint("LEFT", panel.Smaller, "RIGHT", 4, 0)
Paragraph("GameFontDisableSmall", panel.SizeText, 22, L.help)

RefreshPanel = function()
    panel.Status:SetText(Bound() and "|cff40ff40" .. L.ready .. "|r" or "|cffff6060" .. L.notReady .. "|r")
    panel.Send.Refresh()
    panel.ShowBar.Refresh()
    if panel.Translate.GenerateMenu then
        panel.Translate:GenerateMenu()
    else
        panel.Translate:SetText(TypeWhisperDB.translateTo and L.languages[TypeWhisperDB.translateTo] or L.off)
    end
    -- Which LLM translates, as TypeWhisper last wrote it into Status.lua.
    local status = type(TypeWhisperStatus) == "table" and TypeWhisperStatus or nil
    if status and status.ready and status.llm then
        panel.TranslateNote:SetText(format(L.translateWith, status.llm))
    elseif status and TypeWhisperDB.translateTo then
        -- Only a chosen language makes a missing LLM a problem.
        panel.TranslateNote:SetText("|cffff6060" .. (status.llm and format(L.noLlm, status.llm) or L.noLlmNone) .. "|r")
    elseif status then
        panel.TranslateNote:SetText(L.needsLlm)
    else
        panel.TranslateNote:SetText(L.translateNote)
    end
    panel.Apply:SetText(changedThisSession and "|cffffd200" .. L.applyPending .. "|r" or L.applyNote)
    panel.Reload:SetShown(changedThisSession)
    local replyKey = GetBindingKey("REPLY")
    panel.Reply:SetText(replyKey and format(L.replyKey, GetBindingText and GetBindingText(replyKey) or replyKey) or L.replyNoKey)
    panel.MoveButton:SetText(state == "move" and L.lockBar or L.moveBar)
    local percent = math.floor(Scale() * 100 + 0.5)
    panel.SizeText:SetText(format(L.size, percent))
    panel.Smaller:SetEnabled(percent > MIN_SCALE * 100)
    panel.Larger:SetEnabled(percent < MAX_SCALE * 100)
end
panel:SetScript("OnShow", RefreshPanel)

-- Retail, Forever and current Classic list add-on pages in Options › AddOns; older clients use Interface Options.
local category
if Settings and Settings.RegisterCanvasLayoutCategory and Settings.RegisterAddOnCategory then
    category = Settings.RegisterCanvasLayoutCategory(panel, "TypeWhisper")
    Settings.RegisterAddOnCategory(category)
elseif InterfaceOptions_AddCategory then
    panel.name = "TypeWhisper"
    InterfaceOptions_AddCategory(panel)
end

local function OpenSettings()
    if InCombatLockdown() then
        Print(L.combat)
        return
    end
    if category and Settings.OpenToCategory then
        Settings.OpenToCategory(category:GetID())
    elseif InterfaceOptionsFrame_OpenToCategory then
        InterfaceOptionsFrame_OpenToCategory(panel)
    end
end

-- The add-on menu on the minimap (Retail and Forever) calls this global, named in the TOC.
function TypeWhisper_OnAddonCompartmentClick()
    OpenSettings()
end

SLASH_TYPEWHISPER1 = "/typewhisper"
SLASH_TYPEWHISPER2 = "/tw"
SlashCmdList.TYPEWHISPER = function(input)
    local command = strtrim(input or ""):lower()
    if command == "test" then
        TestBar()
    elseif command == "move" then
        ToggleMove()
    elseif command == "reset" then
        ResetPosition()
    elseif command:match("^scale") then
        local percent = tonumber(command:match("^scale%s+(%d+)"))
        if percent then SetBarScale(percent / 100) end
        Print(format(L.size, math.floor(Scale() * 100 + 0.5)))
    else
        Print(Bound() and L.ready or L.notReady)
        OpenSettings()
    end
end

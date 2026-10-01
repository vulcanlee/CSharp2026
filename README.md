# C# 專案範例原始碼

|專案名稱|專案說明|備註|
|-|-|-|
|csFirstAgent|MFA001 - 如何用 Azure OpenAI 的 gpt-5.6-luna 聊天模型建立一個簡單 AI 代理，並產生一篇與鵝有關的詩。||
|csStreamAgent|MFA002 - 改用 RunStreamingAsync 串流輸出回應，文字逐段浮現，並統計首字延遲、總耗時與片段數，可與 csFirstAgent 直接對照。||
|csResponseTokenAgent|MFA003 - 從 LLM 回應中取得 Token 統計：取 OpenAI 原生 SDK 的 ChatTokenUsage，列出輸入／輸出／推論／快取等 token 明細，並附回應中繼資料與台幣費用估算，非串流與串流各示範一次。||
|csLoggingAgent|MFA004 - 用 MAF 原生的 AIAgentBuilder.UseLogging() 在「代理層」掛上日誌，既有程式碼一行都不必改就能看到送進代理的訊息、Options、Metadata 與完整回應 JSON；並說明日誌等級 Debug 與 Trace 的差別。||
|csHttpHandlerAgent|MFA005 - 自訂 DelegatingHandler 搭配 HttpClientPipelineTransport，在 HTTP 傳輸層攔下 MAF 代理真正送出的請求與回應：印出原始 JSON、狀態碼與耗時，並示範金鑰遮蔽與「讀取回應內容會破壞串流」的處理。||
|csConversationAgent|MFA006 - 多回合對話：用 agent.CreateSessionAsync() 建立 AgentSession 並在兩輪之間共用，讓代理記得前一輪說過的話；同時示範不傳 session 就會失憶的對照組，並列出 session 內實際存放的對話歷史。||
|csWorkflowAgent|MFA007 - 用 MAF 的 Workflow 把多個代理接成一張圖：大綱 → 三個寫手平行撰稿 → 扇入組稿 → 審稿，再用帶條件的邊做「過關就發布、沒過就退回重改」的回圈；一次示範序列／扇出／扇入／條件路由與回圈五種結構，並印出 Mermaid 拓撲圖。||
|csPersistSessionAgent|MFA008 - Session 序列化：用 agent.SerializeSessionAsync() 把 AgentSession 存成 JSON 檔，關掉程式再開時用 agent.DeserializeSessionAsync() 還原，讓睡前故事從昨晚的斷點接著講；同時示範不載入存檔的對照組會失憶，並印出存檔大小與 JSON 節錄。||
|csStructuredOutputAgent|MFA009 - 結構化輸出（JSON）：用 `agent.RunAsync<ShoppingList>()` 把食譜轉成五類食材的購物清單，同時顯示模型回傳的 JSON 與 C# 強型別結果。||
|csFunctionCallingAgent|MFA010 - 函式工具呼叫（Function Calling）：用 `AIFunctionFactory.Create()` 把查天氣的 C# 函式（串接 Open-Meteo 真實預報）交給代理，問「明天要穿什麼」時由模型自己決定要不要呼叫、呼叫幾次與參數怎麼填；三題對照分別示範呼叫 1 次、不呼叫與呼叫 2 次。||
|csToolApprovalAgent|MFA011 - 工具核准（代理層的人機互動）：資料庫維運助理把 SELECT／UPDATE／DELETE 三個工具包成 `ApprovalRequiredAIFunction`，再用 `UseToolApproval()` 的 `AutoApprovalRules` 依工具名稱分流：`ExecuteSelect` 自動放行，UPDATE／DELETE 一律跳出 y/n 人工核准，拒絕時可輸入理由回傳給模型；並示範工具內再檢查 SQL 開頭，防止把 DELETE 塞進查詢工具。||
|csRetryMiddlewareAgent|MFA012 - 自己寫中介層（Middleware）：用 `AsBuilder().Use(runFunc: ...)` 掛上自訂的 `RetryIfTooLong`，攔下代理回應檢查字數，超過 100 字上限就在同一個 session 追問「請精簡後重答」一次，重答後仍超標則印出警告照樣回傳；同一個業務方法 `AskAsync()` 分別傳入原始代理與套了中介層的代理做對照，業務程式碼一行都不用改。||
|csCompactionAgent|MFA013 - 對話壓縮：拿一篇六段的長篇訪談稿逐段追問，用 `CompactionProvider` 分別掛上 `SlidingWindowCompactionStrategy`（只留最近 2 個回合）與 `SummarizationCompactionStrategy`（舊回合交給 LLM 濃縮成摘要），和不壓縮的對照組比較每回合輸入 token 與最後「第一段細節題／全篇綜合題」的答題結果；並示範自訂摘要提示詞，避免滾動摘要遺失舊重點或長到省不了 token。||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||
||||

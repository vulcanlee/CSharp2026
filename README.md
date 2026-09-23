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
||||
||||
||||
||||


# 探测语义：劫持关键词分类与置信度标注，Online 判定维持双源任一通过

借鉴 portal 框架的探测语义：最终 URL 含 portal/webauth/drcom/srun 关键词判劫持，单源 204 类探测只算 tentative。我们的外网探测本就是 baidu+microsoft robots.txt 双源加正文内容校验（防门户重定向伪装成 200），没有 204 探测。可借鉴的部分因此收敛为两条：探测禁用自动重定向，3xx 时检查 Location 是否含门户关键词，命中记 HijackSuspected（区别于正文不匹配的 ContentMismatch）；ProbeEvidence 增加置信度标注（当前均为 Confirmed）写进诊断与文档。

Online 判定维持“任一探测通过即在线”。改为“需第二源确认”会降低恢复灵敏度，属于没有事故支撑的行为回归；双源任一通过的语义由正文内容校验保证，疑似劫持已有独立分类兜底。

## Consequences

- 劫持与内容不匹配在日志和诊断包中可区分，网管反馈时能直接指认拦截设备。
- 置信度字段为将来引入宽松单源探测预留了语义位置，不改变当前判定。

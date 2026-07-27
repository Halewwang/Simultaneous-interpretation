#!/usr/bin/env node

import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const generatorVersion = "emke-language-corpus/1.1.0";

const chineseSubjects = [
  "产品团队",
  "设计小组",
  "工程同事",
  "客户代表",
  "会议主持人",
  "项目负责人",
  "测试团队",
  "运营伙伴",
  "研究人员",
  "支持团队",
];
const chinesePredicates = [
  "今天讨论发布计划和风险",
  "正在整理用户反馈和建议",
  "已经确认下一阶段的目标",
  "需要检查翻译结果的准确性",
  "准备记录重要决定和行动项",
  "希望改善会议中的沟通体验",
  "将会验证音频连接是否稳定",
  "正在比较不同方案的优缺点",
  "计划明天完成最后一次测试",
  "同意按照当前节奏继续推进",
];

const englishSubjects = [
  "The product team",
  "The design group",
  "Our engineering partners",
  "The customer representative",
  "The meeting host",
  "The project lead",
  "The quality team",
  "Our operations partners",
  "The research group",
  "The support team",
];
const englishPredicates = [
  "discusses the release plan and risks today",
  "is organizing user feedback and suggestions",
  "has confirmed the goals for the next phase",
  "needs to verify the accuracy of the translation",
  "will record the important decisions and action items",
  "wants to improve communication during the meeting",
  "will verify that the audio connection remains stable",
  "is comparing the advantages of several approaches",
  "plans to complete the final test tomorrow",
  "agrees to continue at the current pace",
];

const germanSubjects = [
  "Das Produktteam",
  "Die Designgruppe",
  "Unsere Entwicklungspartner",
  "Die Kundenvertretung",
  "Die Gesprächsleitung",
  "Die Projektleitung",
  "Das Qualitätsteam",
  "Unsere Betriebspartner",
  "Die Forschungsgruppe",
  "Das Supportteam",
];
const germanPredicates = [
  "bespricht heute den Veröffentlichungsplan und die Risiken",
  "ordnet die Rückmeldungen und Vorschläge der Nutzer",
  "hat die Ziele für die nächste Phase bestätigt",
  "muss die Genauigkeit der Übersetzung überprüfen",
  "notiert die wichtigen Entscheidungen und Aufgaben",
  "möchte die Kommunikation während des Treffens verbessern",
  "prüft die Stabilität der Audioverbindung",
  "vergleicht die Vorteile verschiedener Lösungen",
  "plant morgen den abschließenden Test",
  "stimmt der Fortsetzung im aktuellen Tempo zu",
];

function sentences(subjects, predicates, language) {
  return subjects.flatMap((subject, subjectIndex) =>
    predicates.map((predicate, predicateIndex) => ({
      id: `${language}-${String(subjectIndex * 10 + predicateIndex + 1).padStart(3, "0")}`,
      category: language,
      nativeLanguage: language,
      text: `${subject} ${predicate}${language === "zh" ? "。" : "."}`,
      expectedFinalRoute: "original",
    })),
  );
}

const ambiguous = Array.from({ length: 60 }, (_, index) => ({
  id: `ambiguous-${String(index + 1).padStart(3, "0")}`,
  category: "ambiguous",
  nativeLanguage: ["zh", "en", "de"][index % 3],
  text: String(index + 1).padStart(2, "0"),
  expectedFinalRoute: "undecided",
}));

const outputIndex = process.argv.indexOf("--output");
const outputPath = resolve(
  outputIndex < 0
    ? "/tmp/emke-language-corpus-seed-v1.json"
    : process.argv[outputIndex + 1],
);
if (outputIndex >= 0 && !process.argv[outputIndex + 1]) {
  throw new Error("--output requires a path");
}

const document = {
  contractVersion: 1,
  corpusId: "routing.language-corpus.v1",
  generatorVersion,
  sentenceLicense: "CC0-1.0",
  generationNote:
    "Project-generated synthetic meeting sentences; no external corpus text is included.",
  cases: [
    ...sentences(chineseSubjects, chinesePredicates, "zh"),
    ...sentences(englishSubjects, englishPredicates, "en"),
    ...sentences(germanSubjects, germanPredicates, "de"),
    ...ambiguous,
  ],
};

await mkdir(dirname(outputPath), { recursive: true });
await writeFile(outputPath, `${JSON.stringify(document, null, 2)}\n`);
process.stdout.write(`${document.cases.length} cases -> ${outputPath}\n`);

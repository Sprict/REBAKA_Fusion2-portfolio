import { useState, useCallback, useRef, useEffect } from "react";

const NODES = [
  // === NETWORK LAYER ===
  { id: "GameLauncher", label: "GameLauncher", group: "network", base: "MonoBehaviour", desc: "ネットワーク起動・INetworkRunnerCallbacks" },
  { id: "PlayerSpawner", label: "PlayerSpawner", group: "network", base: "MonoBehaviour", desc: "プレイヤーのSpawn管理" },
  { id: "InputCollector", label: "InputCollector", group: "network", base: "MonoBehaviour", desc: "入力収集・INetworkRunnerCallbacks" },
  { id: "NetworkInputData", label: "NetworkInputData", group: "network", base: "struct", desc: "ネットワーク入力データ構造体" },
  { id: "AprInputBehaviour", label: "AprInputBehaviour", group: "network", base: "MonoBehaviour", desc: "入力収集・INetworkRunnerCallbacks" },

  // === CORE CONTROLLER ===
  { id: "RagdollController", label: "RagdollController", group: "core", base: "NetworkBehaviour", desc: "メインコントローラー（最頻変更）", highlight: true },

  // === PHYSICS LAYER ===
  { id: "RagdollPhysics", label: "RagdollPhysics", group: "physics", base: "class", desc: "物理演算コア（バランス・歩行・パンチ）", highlight: true },
  { id: "RagdollProfile", label: "RagdollProfile", group: "physics", base: "ScriptableObject", desc: "物理パラメータ定義", highlight: true },
  { id: "PidController", label: "PidController", group: "physics", base: "class", desc: "PID制御（Upright/Movement）" },
  { id: "RagdollRigInitializer", label: "RagdollRigInitializer", group: "physics", base: "class", desc: "Rigidbody/Joint初期化" },

  // === STATE LAYER ===
  { id: "RagdollState", label: "RagdollState", group: "state", base: "class", desc: "状態遷移管理" },
  { id: "RagdollStateEvaluator", label: "RagdollStateEvaluator", group: "state", base: "class", desc: "次状態の評価" },
  { id: "PlayerState", label: "PlayerState", group: "state", base: "enum", desc: "Idle/Walk/Ragdoll/GetUp..." },
  { id: "RagdollInput", label: "RagdollInput", group: "state", base: "class", desc: "入力コマンド処理" },

  // === RUNTIME LAYER ===
  { id: "RagdollRuntime", label: "RagdollRuntime", group: "runtime", base: "class", desc: "ホスト側Tick管理" },
  { id: "RagdollClientBootstrapper", label: "RagdollClient\nBootstrapper", group: "runtime", base: "class", desc: "クライアント初期化" },
  { id: "RagdollClientProxyRuntime", label: "RagdollClientProxy\nRuntime", group: "runtime", base: "class", desc: "クライアントProxy更新" },
  { id: "RagdollHostSimulationOrchestrator", label: "HostSimulation\nOrchestrator", group: "runtime", base: "class", desc: "ホストシミュレーション制御" },
  { id: "RagdollProxyPosePublisher", label: "RagdollProxy\nPosePublisher", group: "runtime", base: "class", desc: "Proxy姿勢の同期送信" },

  // === CONTACT LAYER ===
  { id: "RagdollFootContact", label: "RagdollFootContact", group: "contact", base: "NetworkBehaviour", desc: "足の接地検出" },
  { id: "RagdollImpactContact", label: "RagdollImpact\nContact", group: "contact", base: "MonoBehaviour", desc: "衝撃接触検出" },
  { id: "RagdollGroundingService", label: "RagdollGrounding\nService", group: "contact", base: "class", desc: "接地状態サービス" },

  // === CAMERA LAYER ===
  { id: "OrbitCamera", label: "OrbitCamera", group: "camera", base: "MonoBehaviour", desc: "オービットカメラ制御" },
  { id: "LocalPlayerCameraBinder", label: "LocalPlayer\nCameraBinder", group: "camera", base: "MonoBehaviour", desc: "ローカルプレイヤーとカメラ紐付け" },

  // === DIAGNOSTICS ===
  { id: "RagdollDebugView", label: "RagdollDebugView", group: "diag", base: "class", desc: "デバッグGUI表示" },
  { id: "RagdollDiagnosticsReporter", label: "DiagnosticsReporter", group: "diag", base: "class", desc: "診断レポート" },
  { id: "VibrationDiagnostic", label: "VibrationDiagnostic", group: "diag", base: "class", desc: "振動診断" },
];

const EDGES = [
  // RagdollController → コアシステム（中央ハブ）
  { from: "RagdollController", to: "RagdollPhysics", label: "保持・委譲", type: "strong" },
  { from: "RagdollController", to: "RagdollProfile", label: "SerializeField", type: "strong" },
  { from: "RagdollController", to: "RagdollState", label: "保持・委譲", type: "strong" },
  { from: "RagdollController", to: "RagdollInput", label: "保持", type: "normal" },
  { from: "RagdollController", to: "RagdollRuntime", label: "保持", type: "normal" },
  { from: "RagdollController", to: "RagdollClientBootstrapper", label: "保持", type: "normal" },
  { from: "RagdollController", to: "RagdollStateEvaluator", label: "保持", type: "normal" },
  { from: "RagdollController", to: "RagdollGroundingService", label: "保持", type: "normal" },

  // Physics依存
  { from: "RagdollPhysics", to: "RagdollProfile", label: "パラメータ参照", type: "strong" },
  { from: "RagdollPhysics", to: "PidController", label: "使用", type: "normal" },

  // State依存
  { from: "RagdollState", to: "PlayerState", label: "使用", type: "normal" },
  { from: "RagdollStateEvaluator", to: "PlayerState", label: "返す", type: "normal" },
  { from: "RagdollStateEvaluator", to: "RagdollPhysics", label: "参照", type: "normal" },

  // Network → Controller
  { from: "PlayerSpawner", to: "RagdollController", label: "Spawn", type: "normal" },
  { from: "InputCollector", to: "NetworkInputData", label: "収集", type: "normal" },
  { from: "AprInputBehaviour", to: "NetworkInputData", label: "収集", type: "normal" },

  // Contact → Controller
  { from: "RagdollFootContact", to: "RagdollController", label: "IRagdollGroundingSink", type: "interface" },
  { from: "RagdollImpactContact", to: "RagdollController", label: "IRagdollAudioSink", type: "interface" },

  // Runtime
  { from: "RagdollRuntime", to: "NetworkInputData", label: "読み取り", type: "normal" },
  { from: "RagdollHostSimulationOrchestrator", to: "NetworkInputData", label: "読み取り", type: "normal" },
  { from: "RagdollClientProxyRuntime", to: "NetworkInputData", label: "読み取り", type: "normal" },

  // Camera
  { from: "LocalPlayerCameraBinder", to: "OrbitCamera", label: "紐付け", type: "normal" },
  { from: "LocalPlayerCameraBinder", to: "RagdollController", label: "紐付け", type: "normal" },

  // APR base (外部)
  { from: "RagdollRigInitializer", to: "RagdollController", label: "初期化サービス", type: "service" },
];

const GROUP_COLORS = {
  network:  { bg: "#1e3a5f", border: "#4a90d9", text: "#7ec8ff", label: "🌐 Network" },
  core:     { bg: "#3a1a1a", border: "#e05555", text: "#ff9999", label: "⚡ Core" },
  physics:  { bg: "#1a3a1a", border: "#55a855", text: "#99ff99", label: "🔧 Physics" },
  state:    { bg: "#3a2a0a", border: "#d4a020", text: "#ffd070", label: "🔄 State" },
  runtime:  { bg: "#2a1a3a", border: "#9055cc", text: "#cc99ff", label: "⚙️ Runtime" },
  contact:  { bg: "#1a2a3a", border: "#3399cc", text: "#88ccff", label: "👟 Contact" },
  camera:   { bg: "#2a2a1a", border: "#aaaa30", text: "#eeee88", label: "📷 Camera" },
  diag:     { bg: "#2a2a2a", border: "#888888", text: "#cccccc", label: "🔍 Diagnostics" },
};

// Layout positions — grid-checked, no overlaps (NODE_W=130, NODE_H=44, min gap 40px)
// Columns: A=20, B=200, C=390, D=580, E=760, F=930
// Rows:    1=30, 2=160, 3=280, 4=400, 5=510, 6=620
const POSITIONS = {
  // ── Network (Row 1, y=30) ─────────────────────────────────────
  GameLauncher:            { x:  20, y:  30 },  // A1
  PlayerSpawner:           { x: 200, y:  30 },  // B1
  InputCollector:          { x: 390, y:  30 },  // C1
  NetworkInputData:        { x: 570, y:  30 },  // D1
  AprInputBehaviour:       { x: 760, y:  30 },  // E1

  // ── Core (center, Row 2) ─────────────────────────────────────
  RagdollController:       { x: 390, y: 160 },  // C2  ← hub

  // ── Physics (Col A, Rows 2-5) ────────────────────────────────
  RagdollPhysics:          { x:  20, y: 160 },  // A2
  RagdollProfile:          { x:  20, y: 280 },  // A3
  PidController:           { x:  20, y: 400 },  // A4
  RagdollRigInitializer:   { x:  20, y: 510 },  // A5

  // ── Contact (Col B, Rows 3-5) ────────────────────────────────
  RagdollFootContact:      { x: 200, y: 280 },  // B3
  RagdollImpactContact:    { x: 200, y: 400 },  // B4
  RagdollGroundingService: { x: 200, y: 510 },  // B5

  // ── State (Col C-D, Rows 3-4) ────────────────────────────────
  RagdollState:            { x: 390, y: 280 },  // C3
  RagdollInput:            { x: 570, y: 280 },  // D3
  RagdollStateEvaluator:   { x: 390, y: 400 },  // C4
  PlayerState:             { x: 570, y: 400 },  // D4

  // ── Camera (Cols C-D, Row 5) ─────────────────────────────────
  LocalPlayerCameraBinder: { x: 390, y: 510 },  // C5
  OrbitCamera:             { x: 570, y: 510 },  // D5

  // ── Runtime (Cols E-F, Rows 2-5) ─────────────────────────────
  RagdollRuntime:                    { x: 760, y: 160 },  // E2
  RagdollClientBootstrapper:         { x: 930, y: 160 },  // F2
  RagdollHostSimulationOrchestrator: { x: 760, y: 280 },  // E3
  RagdollClientProxyRuntime:         { x: 930, y: 280 },  // F3
  RagdollProxyPosePublisher:         { x: 760, y: 400 },  // E4

  // ── Diagnostics (Col F, Rows 4-6) ────────────────────────────
  RagdollDebugView:           { x: 930, y: 400 },  // F4
  RagdollDiagnosticsReporter: { x: 930, y: 510 },  // F5
  VibrationDiagnostic:        { x: 930, y: 620 },  // F6
};

const NODE_W = 130;
const NODE_H = 44;

function getNodeCenter(id) {
  const p = POSITIONS[id];
  return { x: p.x + NODE_W / 2, y: p.y + NODE_H / 2 };
}

function EdgeArrow({ edge, nodes, isHighlighted, isDimmed }) {
  const from = getNodeCenter(edge.from);
  const to = getNodeCenter(edge.to);

  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const len = Math.sqrt(dx * dx + dy * dy);
  if (len < 1) return null;

  // Shorten to node edge
  const ux = dx / len;
  const uy = dy / len;
  const x1 = from.x + ux * (NODE_W / 2 + 2);
  const y1 = from.y + uy * (NODE_H / 2 + 2);
  const x2 = to.x - ux * (NODE_W / 2 + 8);
  const y2 = to.y - uy * (NODE_H / 2 + 8);

  const colors = {
    strong:    "#e05555",
    normal:    "#6699cc",
    interface: "#44cc88",
    service:   "#cc9944",
  };

  const color = isHighlighted ? "#ffffff" : isDimmed ? "#333333" : (colors[edge.type] || "#6699cc");
  const opacity = isDimmed ? 0.15 : 1;

  // Midpoint for label
  const mx = (x1 + x2) / 2;
  const my = (y1 + y2) / 2;

  return (
    <g opacity={opacity}>
      <defs>
        <marker id={`arrow-${edge.from}-${edge.to}`} markerWidth="8" markerHeight="8"
          refX="4" refY="3" orient="auto">
          <path d="M0,0 L0,6 L8,3 z" fill={color} />
        </marker>
      </defs>
      <line
        x1={x1} y1={y1} x2={x2} y2={y2}
        stroke={color}
        strokeWidth={isHighlighted ? 2.5 : 1.5}
        strokeDasharray={edge.type === "interface" ? "6,3" : edge.type === "service" ? "3,3" : "none"}
        markerEnd={`url(#arrow-${edge.from}-${edge.to})`}
      />
      {isHighlighted && edge.label && (
        <text x={mx} y={my - 5} fill="#ffffff" fontSize="9" textAnchor="middle"
          style={{ pointerEvents: "none", fontFamily: "monospace" }}>
          {edge.label}
        </text>
      )}
    </g>
  );
}

function NodeBox({ node, isSelected, isConnected, isDimmed, onClick }) {
  const pos = POSITIONS[node.id];
  if (!pos) return null;
  const g = GROUP_COLORS[node.group];

  const opacity = isDimmed ? 0.2 : 1;
  const strokeWidth = isSelected ? 3 : isConnected ? 2 : 1;
  const stroke = isSelected ? "#ffffff" : isConnected ? g.border : g.border;
  const glow = isSelected || node.highlight;

  const lines = node.label.split("\n");

  return (
    <g
      transform={`translate(${pos.x}, ${pos.y})`}
      style={{ cursor: "pointer" }}
      onClick={() => onClick(node.id)}
      opacity={opacity}
    >
      {glow && (
        <rect x={-3} y={-3} width={NODE_W + 6} height={NODE_H + 6}
          rx={7} fill="none" stroke={g.border} strokeWidth={2} opacity={0.4}
          style={{ filter: "blur(3px)" }} />
      )}
      <rect x={0} y={0} width={NODE_W} height={NODE_H}
        rx={5} fill={g.bg} stroke={stroke} strokeWidth={strokeWidth} />
      {lines.map((line, i) => (
        <text key={i}
          x={NODE_W / 2}
          y={lines.length === 1 ? NODE_H / 2 + 5 : (i === 0 ? 16 : 30)}
          fill={g.text}
          fontSize={10}
          fontFamily="monospace"
          fontWeight={isSelected ? "bold" : "normal"}
          textAnchor="middle"
          style={{ pointerEvents: "none" }}
        >
          {line}
        </text>
      ))}
      <text x={NODE_W / 2} y={NODE_H + 12}
        fill="#555" fontSize={8} fontFamily="monospace" textAnchor="middle"
        style={{ pointerEvents: "none" }}>
        {node.base}
      </text>
    </g>
  );
}

export default function App() {
  const [selected, setSelected] = useState(null);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [zoom, setZoom] = useState(0.85);
  const [dragging, setDragging] = useState(false);
  const [dragStart, setDragStart] = useState(null);
  const svgRef = useRef(null);

  const selectedNode = selected ? NODES.find(n => n.id === selected) : null;
  const connectedEdges = selected
    ? EDGES.filter(e => e.from === selected || e.to === selected)
    : [];
  const connectedIds = new Set(
    connectedEdges.flatMap(e => [e.from, e.to])
  );

  const handleNodeClick = useCallback((id) => {
    setSelected(prev => prev === id ? null : id);
  }, []);

  const handleMouseDown = (e) => {
    if (e.target === svgRef.current || e.target.tagName === "svg") {
      setDragging(true);
      setDragStart({ x: e.clientX - pan.x, y: e.clientY - pan.y });
    }
  };
  const handleMouseMove = (e) => {
    if (dragging && dragStart) {
      setPan({ x: e.clientX - dragStart.x, y: e.clientY - dragStart.y });
    }
  };
  const handleMouseUp = () => setDragging(false);
  const handleWheel = (e) => {
    e.preventDefault();
    setZoom(z => Math.min(2, Math.max(0.3, z - e.deltaY * 0.001)));
  };

  useEffect(() => {
    const el = svgRef.current;
    if (!el) return;
    el.addEventListener("wheel", handleWheel, { passive: false });
    return () => el.removeEventListener("wheel", handleWheel);
  });

  return (
    <div style={{ background: "#0d1117", minHeight: "100vh", fontFamily: "monospace", color: "#ccc" }}>
      {/* Header */}
      <div style={{ padding: "12px 20px", borderBottom: "1px solid #333", display: "flex", alignItems: "center", gap: "16px" }}>
        <span style={{ fontSize: 16, fontWeight: "bold", color: "#fff" }}>
          🎮 REBAKA_Fusion2 — Script依存関係グラフ
        </span>
        <span style={{ fontSize: 11, color: "#666" }}>クリックでノード選択 / ドラッグでパン / スクロールでズーム</span>
        <div style={{ marginLeft: "auto", display: "flex", gap: "12px", flexWrap: "wrap" }}>
          {Object.entries(GROUP_COLORS).map(([key, g]) => (
            <span key={key} style={{ fontSize: 10, color: g.text, whiteSpace: "nowrap" }}>
              <span style={{ display: "inline-block", width: 8, height: 8, background: g.border, borderRadius: 2, marginRight: 4 }} />
              {g.label}
            </span>
          ))}
        </div>
      </div>

      <div style={{ display: "flex", height: "calc(100vh - 50px)" }}>
        {/* SVG Canvas */}
        <svg
          ref={svgRef}
          style={{ flex: 1, cursor: dragging ? "grabbing" : "grab" }}
          onMouseDown={handleMouseDown}
          onMouseMove={handleMouseMove}
          onMouseUp={handleMouseUp}
          onClick={(e) => { if (e.target === svgRef.current || e.target.tagName === "svg") setSelected(null); }}
        >
          <g transform={`translate(${pan.x}, ${pan.y}) scale(${zoom})`}>
            {/* Edges */}
            {EDGES.map((edge, i) => {
              const isHighlighted = selected && (edge.from === selected || edge.to === selected);
              const isDimmed = selected && !isHighlighted;
              return (
                <EdgeArrow
                  key={i}
                  edge={edge}
                  isHighlighted={isHighlighted}
                  isDimmed={isDimmed}
                />
              );
            })}

            {/* Nodes */}
            {NODES.map(node => {
              const isSelected = selected === node.id;
              const isConnected = connectedIds.has(node.id) && !isSelected;
              const isDimmed = selected && !connectedIds.has(node.id) && !isSelected;
              return (
                <NodeBox
                  key={node.id}
                  node={node}
                  isSelected={isSelected}
                  isConnected={isConnected}
                  isDimmed={isDimmed}
                  onClick={handleNodeClick}
                />
              );
            })}
          </g>
        </svg>

        {/* Side Panel */}
        <div style={{
          width: 280,
          borderLeft: "1px solid #333",
          padding: 16,
          overflowY: "auto",
          background: "#0d1117"
        }}>
          {selectedNode ? (
            <>
              <div style={{
                padding: "8px 12px",
                background: GROUP_COLORS[selectedNode.group].bg,
                border: `1px solid ${GROUP_COLORS[selectedNode.group].border}`,
                borderRadius: 6,
                marginBottom: 16
              }}>
                <div style={{ fontSize: 13, fontWeight: "bold", color: GROUP_COLORS[selectedNode.group].text }}>
                  {selectedNode.label.replace("\n", " ")}
                </div>
                <div style={{ fontSize: 10, color: "#888", marginTop: 4 }}>base: {selectedNode.base}</div>
                <div style={{ fontSize: 11, color: "#aaa", marginTop: 6 }}>{selectedNode.desc}</div>
              </div>

              {connectedEdges.filter(e => e.from === selectedNode.id).length > 0 && (
                <div style={{ marginBottom: 16 }}>
                  <div style={{ fontSize: 11, color: "#888", marginBottom: 8 }}>▶ 依存している（→）</div>
                  {connectedEdges.filter(e => e.from === selectedNode.id).map((e, i) => {
                    const target = NODES.find(n => n.id === e.to);
                    return (
                      <div key={i}
                        style={{ padding: "4px 8px", marginBottom: 4, background: "#1a1a2a",
                          borderRadius: 4, fontSize: 10, cursor: "pointer",
                          border: "1px solid #333", color: "#99ccff" }}
                        onClick={() => setSelected(e.to)}
                      >
                        → {target?.label.replace("\n", " ")}
                        <span style={{ color: "#666", marginLeft: 8 }}>{e.label}</span>
                      </div>
                    );
                  })}
                </div>
              )}

              {connectedEdges.filter(e => e.to === selectedNode.id).length > 0 && (
                <div>
                  <div style={{ fontSize: 11, color: "#888", marginBottom: 8 }}>◀ 参照されている（←）</div>
                  {connectedEdges.filter(e => e.to === selectedNode.id).map((e, i) => {
                    const source = NODES.find(n => n.id === e.from);
                    return (
                      <div key={i}
                        style={{ padding: "4px 8px", marginBottom: 4, background: "#1a2a1a",
                          borderRadius: 4, fontSize: 10, cursor: "pointer",
                          border: "1px solid #333", color: "#99ff99" }}
                        onClick={() => setSelected(e.from)}
                      >
                        ← {source?.label.replace("\n", " ")}
                        <span style={{ color: "#666", marginLeft: 8 }}>{e.label}</span>
                      </div>
                    );
                  })}
                </div>
              )}
            </>
          ) : (
            <div>
              <div style={{ fontSize: 12, color: "#888", marginBottom: 16 }}>
                ノードをクリックして依存関係を確認
              </div>
              <div style={{ fontSize: 11, color: "#666", marginBottom: 12 }}>エッジの凡例:</div>
              {[
                { type: "strong", color: "#e05555", label: "強依存（フィールド保持）", dash: "none" },
                { type: "normal", color: "#6699cc", label: "通常依存（参照・使用）", dash: "none" },
                { type: "interface", color: "#44cc88", label: "インターフェイス経由", dash: "6,3" },
                { type: "service", color: "#cc9944", label: "サービス注入", dash: "3,3" },
              ].map(item => (
                <div key={item.type} style={{ display: "flex", alignItems: "center", marginBottom: 8 }}>
                  <svg width={40} height={12}>
                    <line x1={0} y1={6} x2={36} y2={6} stroke={item.color} strokeWidth={2}
                      strokeDasharray={item.dash} />
                  </svg>
                  <span style={{ fontSize: 10, color: "#888", marginLeft: 8 }}>{item.label}</span>
                </div>
              ))}
              <div style={{ marginTop: 20, padding: "10px", background: "#1a1a1a", borderRadius: 6 }}>
                <div style={{ fontSize: 11, color: "#e05555", marginBottom: 4 }}>⚡ コアトライアド</div>
                <div style={{ fontSize: 10, color: "#aaa" }}>
                  RagdollController<br/>
                  ↕ RagdollPhysics<br/>
                  ↕ RagdollProfile<br/>
                  <span style={{ color: "#666" }}>（常に同時変更）</span>
                </div>
              </div>
              <div style={{ marginTop: 12, padding: "10px", background: "#1a1a1a", borderRadius: 6 }}>
                <div style={{ fontSize: 11, color: "#3399cc", marginBottom: 4 }}>👟 コンタクトペア</div>
                <div style={{ fontSize: 10, color: "#aaa" }}>
                  RagdollFootContact<br/>
                  RagdollImpactContact<br/>
                  <span style={{ color: "#666" }}>（同時変更）</span>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

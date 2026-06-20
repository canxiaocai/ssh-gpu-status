import SwiftUI
import AppKit

/// 表格列宽与间距集中在此，保证各行各列对齐、间距均匀。
private enum Layout {
    static let gpuW: CGFloat = 60      // "GPU 0"
    static let barW: CGFloat = 64      // 进度条
    static let pctW: CGFloat = 34      // "100%"
    static let gibW: CGFloat = 58      // "22.7/48.0G"
    static let tempW: CGFloat = 48     // "100°C"
    static let colGap: CGFloat = 18    // 列间距
    static let inner: CGFloat = 7      // 列内元素间距
    static let rowHPad: CGFloat = 6    // 每行左右内边距（hover 高亮用）
    static let padding: CGFloat = 14   // 面板内边距

    static let memColW = barW + inner + pctW + inner + gibW
    static let utilColW = barW + inner + pctW
}

struct MenuView: View {
    @ObservedObject var state: AppState

    private var panelWidth: CGFloat {
        let m = state.metricVisibility
        var w = Layout.gpuW
        var cols = 1
        if m.memory { w += Layout.memColW; cols += 1 }
        if m.utilization { w += Layout.utilColW; cols += 1 }
        if m.temperature { w += Layout.tempW; cols += 1 }
        w += CGFloat(cols - 1) * Layout.colGap + 2 * Layout.rowHPad
        return max(w + 2 * Layout.padding, 320)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            Divider()

            if state.visibleServers.isEmpty {
                emptyState
            } else {
                VStack(alignment: .leading, spacing: 18) {
                    ForEach(state.orderedStatuses) { status in
                        HostSectionView(status: status, metrics: state.metricVisibility)
                    }
                }
            }

            Divider()
            footer
        }
        .padding(Layout.padding)
        .frame(width: panelWidth)
        .onAppear { state.panelDidOpen() }
        .onDisappear { state.panelDidClose() }
    }

    // MARK: - Sections

    private var header: some View {
        HStack {
            Image(nsImage: Branding.logo)
                .resizable().frame(width: 16, height: 16)
            Text("GPU 状态").font(.headline)
            Spacer()
            Button {
                Task { await state.refreshAll() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("立即刷新全部")
            .disabled(state.visibleServers.isEmpty)
        }
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(state.servers.isEmpty ? "未在 ~/.ssh/config 中找到主机" : "未显示任何服务器")
                .foregroundStyle(.secondary)
            Button("打开设置…") { SettingsWindowManager.shared.show(state: state) }
                .buttonStyle(.borderless)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 6)
    }

    private var footer: some View {
        HStack {
            Button {
                SettingsWindowManager.shared.show(state: state)
            } label: {
                Label("设置", systemImage: "gearshape")
            }
            .buttonStyle(.borderless)

            Spacer()

            Button("退出") { NSApplication.shared.terminate(nil) }
                .buttonStyle(.borderless)
        }
    }
}

// MARK: - Per-host section

struct HostSectionView: View {
    let status: HostStatus
    let metrics: MetricVisibility

    /// 当前 hover 的 GPU 序号（弹出进程窗口）。
    @State private var hovered: Int?
    @State private var hoverTask: Task<Void, Never>?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var uniformModel: String? {
        guard let first = status.gpus.first?.shortName else { return nil }
        return status.gpus.allSatisfy { $0.shortName == first } ? first : nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            hostHeader
            content
        }
    }

    private var hostHeader: some View {
        HStack(spacing: 6) {
            Image(systemName: "server.rack")
                .font(.caption).foregroundStyle(Theme.green)
            Text(status.name)
                .font(.subheadline).bold()
            if let model = uniformModel, !status.gpus.isEmpty {
                Text("· \(status.gpus.count)× \(model)")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            statusIndicator
        }
    }

    @ViewBuilder
    private var statusIndicator: some View {
        if status.isLoading && status.gpus.isEmpty {
            ProgressView().controlSize(.small)
        } else if status.errorMessage != nil {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.caption).foregroundStyle(Theme.red)
        } else if let updated = status.lastUpdated {
            Text(updated.formatted(.dateTime.hour().minute().second()))
                .font(.caption2).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private var content: some View {
        if let error = status.errorMessage {
            VStack(alignment: .leading, spacing: 3) {
                Text(error)
                    .font(.caption).foregroundStyle(Theme.red).lineLimit(3)
                Text("确保能免密 `ssh \(status.name)`，且远程有 nvidia-smi")
                    .font(.caption2).foregroundStyle(.secondary)
            }
        } else if status.gpus.isEmpty {
            if status.isLoading {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("读取中…").font(.caption).foregroundStyle(.secondary)
                }
            } else {
                Text("无 GPU 数据").font(.caption).foregroundStyle(.secondary)
            }
        } else {
            gpuTable
        }
    }

    // MARK: GPU 表格

    private var gpuTable: some View {
        VStack(alignment: .leading, spacing: 2) {
            headerRow
            Divider().padding(.bottom, 2)
            ForEach(status.gpus) { gpu in
                gpuRow(gpu)
            }
        }
    }

    private var headerRow: some View {
        HStack(spacing: Layout.colGap) {
            Text("GPU").frame(width: Layout.gpuW, alignment: .leading)
            if metrics.memory { Text("显存").frame(width: Layout.memColW, alignment: .leading) }
            if metrics.utilization { Text("利用率").frame(width: Layout.utilColW, alignment: .leading) }
            if metrics.temperature { Text("温度").frame(width: Layout.tempW, alignment: .leading) }
        }
        .font(.caption2)
        .foregroundStyle(.tertiary)
        .padding(.horizontal, Layout.rowHPad)
    }

    private func gpuRow(_ gpu: GPUInfo) -> some View {
        HStack(spacing: Layout.colGap) {
            gpuLabel(gpu)
            if metrics.memory { memoryCell(gpu) }
            if metrics.utilization { utilizationCell(gpu) }
            if metrics.temperature { temperatureCell(gpu) }
        }
        .padding(.horizontal, Layout.rowHPad)
        .padding(.vertical, 5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 6)
                .fill(hovered == gpu.index ? Theme.green.opacity(0.10) : Color.clear)
        )
        .contentShape(Rectangle())
        .onHover { inside in handleHover(gpu.index, inside: inside) }
        .popover(isPresented: popoverBinding(gpu), arrowEdge: .trailing) {
            ProcessPopover(gpu: gpu)
        }
    }

    private func gpuLabel(_ gpu: GPUInfo) -> some View {
        HStack(spacing: 6) {
            Circle()
                .fill(gpu.isBusy ? Theme.green : Color.secondary.opacity(0.3))
                .frame(width: 5, height: 5)
            Text("GPU \(gpu.index)")
                .font(.system(.callout, design: .monospaced)).bold()
        }
        .frame(width: Layout.gpuW, alignment: .leading)
    }

    private func memoryCell(_ gpu: GPUInfo) -> some View {
        let color = Theme.memory(gpu.memoryPercent)
        return HStack(spacing: Layout.inner) {
            MetricBar(fraction: gpu.memoryFraction, tint: color).frame(width: Layout.barW)
            Text("\(gpu.memoryPercent)%")
                .font(.caption).monospacedDigit().bold()
                .foregroundStyle(color)
                .frame(width: Layout.pctW, alignment: .trailing)
                .contentTransition(.numericText())
                .animation(reduceMotion ? nil : .default, value: gpu.memoryPercent)
            Text(gpu.memoryGiBText)
                .font(.caption2).monospacedDigit().foregroundStyle(.secondary)
                .frame(width: Layout.gibW, alignment: .leading)
        }
    }

    private func utilizationCell(_ gpu: GPUInfo) -> some View {
        let util = gpu.utilization
        let color = util.map(Theme.utilization) ?? .secondary
        return HStack(spacing: Layout.inner) {
            MetricBar(fraction: util.map { Double($0) / 100 } ?? 0, tint: color).frame(width: Layout.barW)
            Text(util.map { "\($0)%" } ?? "—")
                .font(.caption).monospacedDigit().bold()
                .foregroundStyle(color)
                .frame(width: Layout.pctW, alignment: .trailing)
                .contentTransition(.numericText())
                .animation(reduceMotion ? nil : .default, value: util ?? -1)
        }
    }

    private func temperatureCell(_ gpu: GPUInfo) -> some View {
        let temp = gpu.temperature
        return Text(temp.map { "\($0)°C" } ?? "—")
            .font(.callout).monospacedDigit()
            .foregroundStyle(temp.map(Theme.temperature) ?? .secondary)
            .frame(width: Layout.tempW, alignment: .leading)
            .contentTransition(.numericText())
            .animation(reduceMotion ? nil : .default, value: temp ?? -1)
    }

    // MARK: Hover

    private func handleHover(_ index: Int, inside: Bool) {
        if inside {
            hoverTask?.cancel()
            hoverTask = Task {
                try? await Task.sleep(for: .milliseconds(220))
                if !Task.isCancelled { hovered = index }
            }
        } else {
            hoverTask?.cancel()
            if hovered == index { hovered = nil }
        }
    }

    private func popoverBinding(_ gpu: GPUInfo) -> Binding<Bool> {
        Binding(
            get: { hovered == gpu.index && !gpu.processes.isEmpty },
            set: { show in if !show && hovered == gpu.index { hovered = nil } }
        )
    }
}

// MARK: - 进程弹窗

struct ProcessPopover: View {
    let gpu: GPUInfo

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "memorychip").foregroundStyle(Theme.green)
                Text("GPU \(gpu.index)")
                    .font(.system(.subheadline, design: .monospaced)).bold()
                Text(gpu.shortName)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 12)
                Text("\(gpu.processes.count) 个进程")
                    .font(.caption2).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)

            Divider()

            VStack(spacing: 0) {
                ForEach(Array(gpu.processes.enumerated()), id: \.element.id) { idx, p in
                    if idx > 0 { Divider().padding(.leading, 52) }
                    processRow(p)
                }
            }
        }
        .frame(width: 330)
    }

    private func processRow(_ p: GPUProcess) -> some View {
        HStack(spacing: 11) {
            ZStack {
                Circle().fill(Theme.green.opacity(0.15)).frame(width: 30, height: 30)
                Text(initials(p.user))
                    .font(.caption2).bold().foregroundStyle(Theme.green)
            }
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(p.user).font(.caption).bold()
                    Text(p.shortName).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                MetricBar(fraction: Double(p.memoryUsed) / max(1, Double(gpu.memoryTotal)), tint: Theme.green)
                    .frame(width: 160, height: 4)
            }
            Spacer(minLength: 10)
            VStack(alignment: .trailing, spacing: 3) {
                Text(memText(p.memoryUsed))
                    .font(.caption).monospacedDigit().bold()
                Text(verbatim: "pid \(p.pid)")
                    .font(.caption2).monospacedDigit().foregroundStyle(.tertiary)
            }
            .fixedSize()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .help(p.name)
    }

    private func initials(_ user: String) -> String {
        user == "?" ? "?" : String(user.prefix(2)).uppercased()
    }

    private func memText(_ mib: Int) -> String {
        mib >= 1024 ? String(format: "%.1f GiB", Double(mib) / 1024) : "\(mib) MiB"
    }
}

// MARK: - Metric bar

struct MetricBar: View {
    let fraction: Double
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var clamped: Double { max(0, min(1, fraction)) }

    var body: some View {
        GeometryReader { geo in
            // fraction > 0 时保证最小可见宽度，避免低占用时填充塌成一个点「消失」。
            let width = clamped > 0 ? max(3, clamped * geo.size.width) : 0
            ZStack(alignment: .leading) {
                Capsule().fill(Theme.track)
                Capsule()
                    .fill(LinearGradient(colors: [tint.opacity(0.85), tint],
                                         startPoint: .leading, endPoint: .trailing))
                    .frame(width: width)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.35), value: clamped)
            }
        }
        .frame(height: 6)
    }
}

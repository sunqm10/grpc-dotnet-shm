#!/usr/bin/env python3
"""
gRPC .NET Shared Memory Benchmark Plot Generator

Generates visualization plots from benchmark results JSON file.
"""

import json
import os
import sys
from datetime import datetime
from pathlib import Path

try:
    import matplotlib.pyplot as plt
    import matplotlib.ticker as ticker
    import numpy as np
except ImportError:
    print("Error: matplotlib and numpy are required. Install with:")
    print("  pip install matplotlib numpy")
    sys.exit(1)

# Plot style configuration
plt.style.use('seaborn-v0_8-whitegrid')
COLORS = {
    'tcp': '#3498db',      # Blue
    'shm': '#e74c3c',      # Red
    'tcp_light': '#85c1e9',
    'shm_light': '#f1948a'
}

def load_results(filepath):
    """Load benchmark results from JSON file."""
    with open(filepath, 'r') as f:
        return json.load(f)

def format_size(size_bytes):
    """Format byte size to human readable string."""
    if size_bytes >= 1024 * 1024:
        return f"{size_bytes // (1024 * 1024)}MB"
    elif size_bytes >= 1024:
        return f"{size_bytes // 1024}KB"
    else:
        return f"{size_bytes}B"

def plot_throughput_comparison(results, output_dir, workload='unary'):
    """Create throughput comparison bar chart for TCP vs SHM."""
    fig, axes = plt.subplots(1, 2, figsize=(14, 6))
    
    # Ensure axes is always indexable
    if not hasattr(axes, '__len__'):
        axes = [axes]
    
    # Filter results by workload
    filtered = [r for r in results if r['Workload'] == workload]
    
    # Get unique message sizes and concurrency levels
    sizes = sorted(set(r['MessageSize'] for r in filtered))
    concurrencies = sorted(set(r['Concurrency'] for r in filtered))
    
    for idx, concurrency in enumerate(concurrencies[:2]):  # Plot first 2 concurrency levels
        if idx >= len(axes):
            break
        ax = axes[idx]
        
        tcp_throughput = []
        shm_throughput = []
        size_labels = []
        
        for size in sizes:
            tcp_result = next((r for r in filtered if r['Transport'] == 'tcp' 
                              and r['MessageSize'] == size 
                              and r['Concurrency'] == concurrency), None)
            shm_result = next((r for r in filtered if r['Transport'] == 'shm' 
                              and r['MessageSize'] == size 
                              and r['Concurrency'] == concurrency), None)
            
            if tcp_result and shm_result:
                tcp_throughput.append(tcp_result['ThroughputMBps'])
                shm_throughput.append(shm_result['ThroughputMBps'])
                size_labels.append(format_size(size))
        
        if not size_labels:
            continue
            
        x = np.arange(len(size_labels))
        width = 0.35
        
        ax.bar(x - width/2, tcp_throughput, width, label='TCP', color=COLORS['tcp'])
        ax.bar(x + width/2, shm_throughput, width, label='Shared Memory', color=COLORS['shm'])
        
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title(f'{workload.capitalize()} RPC - {concurrency} Concurrent Calls')
        ax.set_xticks(x)
        ax.set_xticklabels(size_labels, rotation=45)
        ax.legend()
        ax.grid(True, alpha=0.3)
    
    plt.suptitle(f'gRPC .NET Throughput: TCP vs Shared Memory ({workload.capitalize()})', fontsize=14, fontweight='bold')
    plt.tight_layout()
    plt.savefig(os.path.join(output_dir, f'throughput_{workload}.png'), dpi=150, bbox_inches='tight')
    plt.close()
    print(f"  Generated: throughput_{workload}.png")

def plot_latency_comparison(results, output_dir, workload='unary'):
    """Create latency comparison chart for TCP vs SHM."""
    fig, axes = plt.subplots(1, 2, figsize=(14, 6))
    
    # Ensure axes is always indexable
    if not hasattr(axes, '__len__'):
        axes = [axes]
    
    filtered = [r for r in results if r['Workload'] == workload]
    
    sizes = sorted(set(r['MessageSize'] for r in filtered))
    concurrencies = sorted(set(r['Concurrency'] for r in filtered))
    
    for idx, concurrency in enumerate(concurrencies[:2]):
        if idx >= len(axes):
            break
        ax = axes[idx]
        
        tcp_p50 = []
        tcp_p99 = []
        shm_p50 = []
        shm_p99 = []
        size_labels = []
        
        for size in sizes:
            tcp_result = next((r for r in filtered if r['Transport'] == 'tcp' 
                              and r['MessageSize'] == size 
                              and r['Concurrency'] == concurrency), None)
            shm_result = next((r for r in filtered if r['Transport'] == 'shm' 
                              and r['MessageSize'] == size 
                              and r['Concurrency'] == concurrency), None)
            
            if tcp_result and shm_result:
                tcp_p50.append(tcp_result['LatencyP50Us'])
                tcp_p99.append(tcp_result['LatencyP99Us'])
                shm_p50.append(shm_result['LatencyP50Us'])
                shm_p99.append(shm_result['LatencyP99Us'])
                size_labels.append(format_size(size))
        
        if not size_labels:
            continue
        
        x = np.arange(len(size_labels))
        width = 0.35
        
        # Plot P50 and P99 as grouped bars with error-like appearance
        ax.bar(x - width/2, tcp_p50, width, label='TCP P50', color=COLORS['tcp'])
        ax.bar(x - width/2, np.array(tcp_p99) - np.array(tcp_p50), width, 
               bottom=tcp_p50, label='TCP P99', color=COLORS['tcp_light'], alpha=0.7)
        
        ax.bar(x + width/2, shm_p50, width, label='SHM P50', color=COLORS['shm'])
        ax.bar(x + width/2, np.array(shm_p99) - np.array(shm_p50), width, 
               bottom=shm_p50, label='SHM P99', color=COLORS['shm_light'], alpha=0.7)
        
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (µs)')
        ax.set_title(f'{workload.capitalize()} RPC - {concurrency} Concurrent Calls')
        ax.set_xticks(x)
        ax.set_xticklabels(size_labels, rotation=45)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
        ax.grid(True, alpha=0.3, which='both')
    
    plt.suptitle(f'gRPC .NET Latency: TCP vs Shared Memory ({workload.capitalize()})', fontsize=14, fontweight='bold')
    plt.tight_layout()
    plt.savefig(os.path.join(output_dir, f'latency_{workload}.png'), dpi=150, bbox_inches='tight')
    plt.close()
    print(f"  Generated: latency_{workload}.png")

def plot_speedup(results, output_dir):
    """Create speedup chart showing SHM improvement over TCP."""
    fig, axes = plt.subplots(1, 2, figsize=(14, 6))
    
    workloads = ['unary', 'streaming']
    
    for idx, workload in enumerate(workloads):
        ax = axes[idx]
        
        filtered = [r for r in results if r['Workload'] == workload]
        sizes = sorted(set(r['MessageSize'] for r in filtered))
        concurrencies = sorted(set(r['Concurrency'] for r in filtered))
        
        for conc_idx, concurrency in enumerate(concurrencies[:3]):
            speedups = []
            size_labels = []
            
            for size in sizes:
                tcp_result = next((r for r in filtered if r['Transport'] == 'tcp' 
                                  and r['MessageSize'] == size 
                                  and r['Concurrency'] == concurrency), None)
                shm_result = next((r for r in filtered if r['Transport'] == 'shm' 
                                  and r['MessageSize'] == size 
                                  and r['Concurrency'] == concurrency), None)
                
                if tcp_result and shm_result and tcp_result['OpsPerSecond'] > 0:
                    speedups.append(shm_result['OpsPerSecond'] / tcp_result['OpsPerSecond'])
                    size_labels.append(format_size(size))
            
            if speedups:
                ax.plot(size_labels, speedups, marker='o', linewidth=2, 
                       label=f'{concurrency} concurrent', markersize=8)
        
        ax.axhline(y=1.0, color='gray', linestyle='--', alpha=0.5, label='1x (baseline)')
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Speedup (SHM / TCP)')
        ax.set_title(f'{workload.capitalize()} RPC')
        ax.legend()
        ax.grid(True, alpha=0.3)
        ax.set_xticklabels(size_labels, rotation=45)
    
    plt.suptitle('Shared Memory Speedup over TCP', fontsize=14, fontweight='bold')
    plt.tight_layout()
    plt.savefig(os.path.join(output_dir, 'speedup.png'), dpi=150, bbox_inches='tight')
    plt.close()
    print("  Generated: speedup.png")

def plot_ops_per_second(results, output_dir):
    """Create operations per second comparison."""
    fig, axes = plt.subplots(2, 2, figsize=(14, 12))
    
    workloads = ['unary', 'streaming']
    transports = ['tcp', 'shm']
    
    for w_idx, workload in enumerate(workloads):
        for t_idx, transport in enumerate(transports):
            ax = axes[w_idx][t_idx]
            
            filtered = [r for r in results if r['Workload'] == workload and r['Transport'] == transport]
            sizes = sorted(set(r['MessageSize'] for r in filtered))
            concurrencies = sorted(set(r['Concurrency'] for r in filtered))
            
            for concurrency in concurrencies:
                ops = []
                size_labels = []
                
                for size in sizes:
                    result = next((r for r in filtered if r['MessageSize'] == size 
                                  and r['Concurrency'] == concurrency), None)
                    if result:
                        ops.append(result['OpsPerSecond'])
                        size_labels.append(format_size(size))
                
                if ops:
                    ax.plot(size_labels, ops, marker='o', linewidth=2, 
                           label=f'{concurrency} concurrent', markersize=6)
            
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Operations/Second')
            ax.set_title(f'{workload.capitalize()} - {transport.upper()}')
            ax.legend()
            ax.grid(True, alpha=0.3)
            ax.set_yscale('log')
            ax.yaxis.set_major_formatter(ticker.FuncFormatter(lambda x, p: format(int(x), ',')))
            plt.setp(ax.get_xticklabels(), rotation=45)
    
    plt.suptitle('Operations Per Second by Message Size and Concurrency', fontsize=14, fontweight='bold')
    plt.tight_layout()
    plt.savefig(os.path.join(output_dir, 'ops_per_second.png'), dpi=150, bbox_inches='tight')
    plt.close()
    print("  Generated: ops_per_second.png")

def plot_latency_distribution(results, output_dir):
    """Create latency distribution comparison."""
    fig, ax = plt.subplots(figsize=(12, 6))
    
    # Focus on single concurrency level and multiple message sizes
    concurrency = 1
    workload = 'unary'
    
    filtered = [r for r in results if r['Workload'] == workload and r['Concurrency'] == concurrency]
    sizes = sorted(set(r['MessageSize'] for r in filtered))[:5]  # First 5 sizes
    
    x = np.arange(len(sizes))
    width = 0.35
    
    tcp_latencies = []
    shm_latencies = []
    size_labels = []
    
    for size in sizes:
        tcp_result = next((r for r in filtered if r['Transport'] == 'tcp' and r['MessageSize'] == size), None)
        shm_result = next((r for r in filtered if r['Transport'] == 'shm' and r['MessageSize'] == size), None)
        
        if tcp_result and shm_result:
            tcp_latencies.append([tcp_result['LatencyMinUs'], tcp_result['LatencyP50Us'], 
                                 tcp_result['LatencyP90Us'], tcp_result['LatencyP99Us'], 
                                 tcp_result['LatencyMaxUs']])
            shm_latencies.append([shm_result['LatencyMinUs'], shm_result['LatencyP50Us'], 
                                 shm_result['LatencyP90Us'], shm_result['LatencyP99Us'], 
                                 shm_result['LatencyMaxUs']])
            size_labels.append(format_size(size))
    
    if tcp_latencies:
        tcp_data = np.array(tcp_latencies)
        shm_data = np.array(shm_latencies)
        
        # Create box-like visualization
        for i, label in enumerate(size_labels):
            # TCP
            ax.vlines(i - width/2, tcp_data[i, 0], tcp_data[i, 4], color=COLORS['tcp'], linewidth=1)
            ax.hlines([tcp_data[i, 0], tcp_data[i, 4]], i - width/2 - 0.05, i - width/2 + 0.05, color=COLORS['tcp'])
            ax.bar(i - width/2, tcp_data[i, 3] - tcp_data[i, 1], width * 0.8, bottom=tcp_data[i, 1], 
                   color=COLORS['tcp_light'], edgecolor=COLORS['tcp'], linewidth=1)
            ax.scatter([i - width/2], [tcp_data[i, 1]], color=COLORS['tcp'], s=50, zorder=5)
            
            # SHM
            ax.vlines(i + width/2, shm_data[i, 0], shm_data[i, 4], color=COLORS['shm'], linewidth=1)
            ax.hlines([shm_data[i, 0], shm_data[i, 4]], i + width/2 - 0.05, i + width/2 + 0.05, color=COLORS['shm'])
            ax.bar(i + width/2, shm_data[i, 3] - shm_data[i, 1], width * 0.8, bottom=shm_data[i, 1], 
                   color=COLORS['shm_light'], edgecolor=COLORS['shm'], linewidth=1)
            ax.scatter([i + width/2], [shm_data[i, 1]], color=COLORS['shm'], s=50, zorder=5)
    
    ax.set_xlabel('Message Size')
    ax.set_ylabel('Latency (µs)')
    ax.set_title(f'Latency Distribution (P50 to P99) - {workload.capitalize()}, {concurrency} Concurrent')
    ax.set_xticks(np.arange(len(size_labels)))
    ax.set_xticklabels(size_labels)
    ax.set_yscale('log')
    ax.grid(True, alpha=0.3, which='both')
    
    # Custom legend
    from matplotlib.patches import Patch
    legend_elements = [
        Patch(facecolor=COLORS['tcp_light'], edgecolor=COLORS['tcp'], label='TCP (P50-P99)'),
        Patch(facecolor=COLORS['shm_light'], edgecolor=COLORS['shm'], label='SHM (P50-P99)')
    ]
    ax.legend(handles=legend_elements)
    
    plt.tight_layout()
    plt.savefig(os.path.join(output_dir, 'latency_distribution.png'), dpi=150, bbox_inches='tight')
    plt.close()
    print("  Generated: latency_distribution.png")

def generate_summary_table(results, output_dir):
    """Generate a summary markdown table."""
    summary = "# gRPC .NET Shared Memory Benchmark Results\n\n"
    summary += f"Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n\n"
    
    summary += "## Summary\n\n"
    summary += "| Transport | Workload | Size | Concurrency | Ops/s | MB/s | P50 (µs) | P99 (µs) |\n"
    summary += "|-----------|----------|------|-------------|-------|------|----------|----------|\n"
    
    for r in sorted(results, key=lambda x: (x['Transport'], x['Workload'], x['MessageSize'], x['Concurrency'])):
        size_str = format_size(r['MessageSize'])
        summary += f"| {r['Transport'].upper()} | {r['Workload']} | {size_str} | {r['Concurrency']} | "
        summary += f"{r['OpsPerSecond']:,.0f} | {r['ThroughputMBps']:.1f} | "
        summary += f"{r['LatencyP50Us']:.0f} | {r['LatencyP99Us']:.0f} |\n"
    
    # Add speedup section
    summary += "\n## Speedup (SHM vs TCP)\n\n"
    summary += "| Workload | Size | Concurrency | Throughput Speedup | Latency Improvement |\n"
    summary += "|----------|------|-------------|-------------------|---------------------|\n"
    
    tcp_results = [r for r in results if r['Transport'] == 'tcp']
    shm_results = [r for r in results if r['Transport'] == 'shm']
    
    for shm in shm_results:
        tcp = next((t for t in tcp_results if t['Workload'] == shm['Workload'] 
                   and t['MessageSize'] == shm['MessageSize'] 
                   and t['Concurrency'] == shm['Concurrency']), None)
        if tcp and tcp['OpsPerSecond'] > 0:
            speedup = shm['OpsPerSecond'] / tcp['OpsPerSecond']
            latency_improvement = tcp['LatencyP50Us'] / shm['LatencyP50Us'] if shm['LatencyP50Us'] > 0 else 0
            size_str = format_size(shm['MessageSize'])
            summary += f"| {shm['Workload']} | {size_str} | {shm['Concurrency']} | {speedup:.2f}x | {latency_improvement:.2f}x |\n"
    
    with open(os.path.join(output_dir, 'summary.md'), 'w') as f:
        f.write(summary)
    print("  Generated: summary.md")

def main():
    if len(sys.argv) < 2:
        results_file = "results/benchmark_results.json"
    else:
        results_file = sys.argv[1]
    
    if len(sys.argv) < 3:
        output_dir = "plots"
    else:
        output_dir = sys.argv[2]
    
    # Create output directory if needed
    Path(output_dir).mkdir(parents=True, exist_ok=True)
    
    print(f"Loading results from: {results_file}")
    
    if not os.path.exists(results_file):
        print(f"Error: Results file not found: {results_file}")
        sys.exit(1)
    
    results = load_results(results_file)
    print(f"Loaded {len(results)} benchmark results")
    
    print("\nGenerating plots...")
    
    # Generate all plots
    if any(r['Workload'] == 'unary' for r in results):
        plot_throughput_comparison(results, output_dir, 'unary')
        plot_latency_comparison(results, output_dir, 'unary')
    
    if any(r['Workload'] == 'streaming' for r in results):
        plot_throughput_comparison(results, output_dir, 'streaming')
        plot_latency_comparison(results, output_dir, 'streaming')
    
    if len(set(r['Transport'] for r in results)) > 1:
        plot_speedup(results, output_dir)
    
    plot_ops_per_second(results, output_dir)
    plot_latency_distribution(results, output_dir)
    generate_summary_table(results, output_dir)
    
    print(f"\nAll plots saved to: {output_dir}/")

if __name__ == "__main__":
    main()

using Voice2Txt.Core.Storage;

namespace Voice2Txt.Core.Abstractions;

/// <summary>녹음 메타데이터 저장소. 구현체는 SQLite 기반.</summary>
public interface IRecordingStore
{
    /// <summary>스키마를 준비한다(없으면 생성).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>모든 녹음을 최신순으로 반환.</summary>
    Task<IReadOnlyList<Recording>> GetAllAsync(CancellationToken ct = default);

    /// <summary>녹음 1건 추가.</summary>
    Task AddAsync(Recording recording, CancellationToken ct = default);

    /// <summary>녹음 메타데이터 갱신(변환여부 등).</summary>
    Task UpdateAsync(Recording recording, CancellationToken ct = default);

    /// <summary>녹음 1건 삭제.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}

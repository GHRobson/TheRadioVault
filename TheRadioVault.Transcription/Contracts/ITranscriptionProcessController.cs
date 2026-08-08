namespace TheRadioVault.Transcription.Contracts;

public interface ITranscriptionProcessController
{
    bool TryPause(int processId);
    bool TryResume(int processId);
}

public interface IPausableTranscriptionEngine
{
    bool Pause(Guid operationId);
    bool Resume(Guid operationId);
}

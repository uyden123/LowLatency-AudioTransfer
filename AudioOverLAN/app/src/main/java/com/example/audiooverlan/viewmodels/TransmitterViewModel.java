package com.example.audiooverlan.viewmodels;

import androidx.lifecycle.LiveData;
import androidx.lifecycle.ViewModel;

import com.example.audiooverlan.services.AudioTransmitterService;

public class TransmitterViewModel extends ViewModel {
    private final TransmitterStateRepository repository;

    public TransmitterViewModel() {
        this.repository = TransmitterStateRepository.getInstance();
    }

    public LiveData<TransmitterState> getTransmitterState() {
        return repository.getTransmitterState();
    }

    public boolean isServiceRunning() {
        return AudioTransmitterService.isServiceRunning;
    }
}

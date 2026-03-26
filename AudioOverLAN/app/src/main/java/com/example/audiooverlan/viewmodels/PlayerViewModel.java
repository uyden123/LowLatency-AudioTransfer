package com.example.audiooverlan.viewmodels;

import androidx.lifecycle.LiveData;
import androidx.lifecycle.ViewModel;

public class PlayerViewModel extends ViewModel {
    private final PlayerStateRepository repository = PlayerStateRepository.getInstance();

    public LiveData<PlayerState> getPlayerState() {
        return repository.getPlayerState();
    }
}

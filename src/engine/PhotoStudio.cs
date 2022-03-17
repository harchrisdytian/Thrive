﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

/// <summary>
///   Creates images of game resources by rendering them in a separate viewport
/// </summary>
/// <remarks>
///   <para>
///     TODO: implement a persistent resource cache class that can store these created images (it should be cleared
///     if game version changes to avoid bugs and size limited to not take too many gigabytes of space)
///   </para>
/// </remarks>
public class PhotoStudio : Viewport
{
    [Export]
    public NodePath CameraPath = null!;

    [Export]
    public NodePath RenderedObjectHolderPath = null!;

    [Export]
    public bool UseBackgroundSceneLoad;

    [Export]
    public bool UseBackgroundSceneInstance;

    private readonly Queue<ImageTask> tasks = new();
    private ImageTask? currentTask;
    private Step currentTaskStep = Step.NoTask;

    private bool waitingForBackgroundOperation;

    private PackedScene? taskScene;
    private string? loadedTaskScene;
    private bool previousSceneWasCorrect;

    private Spatial? instancedScene;

    private Camera camera = null!;
    private Spatial renderedObjectHolder = null!;

    private PhotoStudio? instance;

    private PhotoStudio() { }

    private enum Step
    {
        NoTask,
        LoadScene,
        InstanceScene,
        ApplySceneParameters,
        AttachScene,
        Render,
        Save,
    }

    public PhotoStudio Instance => instance ?? throw new InstanceNotLoadedYetException();

    public override void _Ready()
    {
        instance = this;

        base._Ready();

        camera = GetNode<Camera>(CameraPath);
        renderedObjectHolder = GetNode<Spatial>(RenderedObjectHolderPath);
    }

    public override void _Process(float delta)
    {
        if (currentTaskStep == Step.NoTask)
        {
            // Try to start a task or do nothing if there aren't any
            if (tasks.Count > 0)
            {
                currentTask = tasks.Dequeue();
                currentTaskStep = Step.LoadScene;
                previousSceneWasCorrect = false;
            }
        }

        Step? lateSetNextStep = null;

        switch (currentTaskStep)
        {
            case Step.NoTask:
                break;
            case Step.LoadScene:
            {
                if (UseBackgroundSceneLoad)
                {
                    if (!waitingForBackgroundOperation)
                    {
                        waitingForBackgroundOperation = true;
                        TaskExecutor.Instance.AddTask(new Task(LoadCurrentTaskScene));
                    }
                }
                else
                {
                    LoadCurrentTaskScene();
                }

                break;
            }

            case Step.InstanceScene:
            {
                if (UseBackgroundSceneInstance)
                {
                    if (!waitingForBackgroundOperation)
                    {
                        waitingForBackgroundOperation = true;
                        TaskExecutor.Instance.AddTask(new Task(InstanceCurrentScene));
                    }
                }
                else
                {
                    InstanceCurrentScene();
                }

                break;
            }

            case Step.ApplySceneParameters:
            {
                currentTask!.Photographable.ApplySceneParameters(instancedScene ??
                    throw new Exception("scene was not instanced when expected"));
                currentTaskStep = Step.AttachScene;
                break;
            }

            case Step.AttachScene:
            {
                // Only need to swap scenes if the new image is of a different type of thing than what we had previously
                if (!previousSceneWasCorrect)
                {
                    renderedObjectHolder.FreeChildren();
                    renderedObjectHolder.AddChild(instancedScene);
                }

                currentTaskStep = Step.Render;
                break;
            }

            case Step.Render:
            {
                camera.Translation = new Vector3(0,
                    currentTask!.Photographable.CalculatePhotographDistance(instancedScene!), 0);
                RenderTargetUpdateMode = UpdateMode.Once;
                lateSetNextStep = Step.Save;
                break;
            }

            case Step.Save:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        base._Process(delta);

        if (currentTaskStep == Step.Save)
        {
            var image = GetTexture().GetData();
            image.Convert(Image.Format.Rgba8);

            var texture = new ImageTexture();
            texture.CreateFromImage(image, (uint)Texture.FlagsEnum.Filter | (uint)Texture.FlagsEnum.Mipmaps);

            currentTask!.OnFinished(texture);

            currentTask = null;
            currentTaskStep = Step.NoTask;

            // TODO: debugging code, remove
            image.SavePng("/home/hhyyrylainen/Projects/Thrive/test.png");
        }

        if (lateSetNextStep != null)
            currentTaskStep = lateSetNextStep.Value;
    }

    /// <summary>
    ///   Starts an image creation task
    /// </summary>
    /// <param name="task">The task to queue and run as soon as possible</param>
    public void SubmitTask(ImageTask task)
    {
        tasks.Enqueue(task);
    }

    /// <summary>
    ///   Starts an image creation for a thing that can be photographed
    /// </summary>
    /// <param name="photographable">The object to create and start an <see cref="ImageTask"/> for</param>
    public void SubmitTask(IPhotographable photographable)
    {
        tasks.Enqueue(new ImageTask(photographable));
    }

    private void LoadCurrentTaskScene()
    {
        var wantedScenePath = currentTask!.Photographable.SceneToPhotographPath;

        if (wantedScenePath == loadedTaskScene)
        {
            previousSceneWasCorrect = true;
            currentTaskStep = Step.ApplySceneParameters;
        }
        else
        {
            taskScene = GD.Load<PackedScene>(wantedScenePath);
            loadedTaskScene = wantedScenePath;
            previousSceneWasCorrect = false;
            currentTaskStep = Step.InstanceScene;
        }

        waitingForBackgroundOperation = false;
    }

    private void InstanceCurrentScene()
    {
        instancedScene = taskScene!.Instance<Spatial>();

        waitingForBackgroundOperation = false;
        currentTaskStep = Step.ApplySceneParameters;
    }
}
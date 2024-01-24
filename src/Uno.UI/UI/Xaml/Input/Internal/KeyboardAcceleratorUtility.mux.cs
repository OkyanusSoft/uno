﻿#nullable enable

using Uno.UI.DirectUI;
using Uno.UI.Extensions;
using Uno.UI.Xaml.Input;
using Windows.System;

namespace Microsoft.UI.Xaml.Input;

internal static class KeyboardAcceleratorUtility
{
	// Through TryInvokeKeyboardAccelerator mechanism we will be traversing the whole subtree to hunt for the accelerator.
	// As a result we may encounter matching accelerator but with the scope not valid for this instance.
	// Below utility function will validate the scope of an accelerator before raising the event.
	internal static bool IsAcceleratorLocallyScoped(
		  DependencyObject pFocusedElement,
		  KeyboardAccelerator accelerator)
	{
		// If unable to retrive the focused element, consider the accelerator scope as valid scope.
		if (pFocusedElement == null)
		{
			return false;
		}
		if (accelerator.m_scopeOwner == null)
		{
			return false; // accelerator is global, hence okay to be invoked.
		}

		// Below code will unwrap the managed object pointer for scopeOwner and returns actual pointer to compare with Owner.
		/*VERIFYHR*/
		var value = accelerator.GetValue(KeyboardAccelerator.ScopeOwnerProperty);
		DependencyObject? pDO = value as DependencyObject;
		// If the scope owner for accelerator matches with starting UIElement, accelerator is okay to be invoked.
		if (pDO == pFocusedElement)
		{
			return false;
		}

		// Check if the scope owner is one of the UIElement in the path from focused element to the root.
		// If it is then the accelerator is not locally scoped and we are good to invoke it.
		DependencyObject? parent = pFocusedElement.GetParentInternal(false /* publicParentOnly */);
		while (parent != null)
		{
			if (parent == pDO)
			{
				return false;
			}
			parent = parent.GetParentInternal(false /* publicParentOnly */);
		}

		// As we are here, this means accelerator is locally scoped and we should not call it.
		return true;
	}

	internal static bool RaiseKeyboardAcceleratorInvoked(
		 KeyboardAccelerator pKAccelerator,
		 DependencyObject? pKAcceleratorParentElement)
	{
		bool isHandled = false;

		/*VERIFYHR*/
		try
		{
			isHandled = FxCallbacks.KeyboardAccelerator_RaiseKeyboardAcceleratorInvoked(pKAccelerator, pKAcceleratorParentElement);
		}
		catch
		{
			return false;
		}

		// If not handled, invoke the default action on the parent UI Element. e.g. Click on Button.
		if (!isHandled)
		{
			isHandled = KeyboardAutomationInvoker.InvokeAutomationAction(pKAcceleratorParentElement as FrameworkElement);
		}

		return isHandled;
	}

	internal static bool ProcessAllLiveAccelerators(
		VirtualKey originalKey,
		VirtualKeyModifiers keyModifiers,
		VectorOfKACollectionAndRefCountPair& allLiveAccelerators,
		DependencyObject owner,
		KeyboardAcceleratorPolicyFn policyFn,
		DependencyObject pFocusedElement,
		bool isCallFromTryInvoke)
	{
		Jupiter.stack_vector < KACollectionAndRefCountPair, 25 > collectionsToGC;
		foreach (var weakCollection in allLiveAccelerators)
		{
			xref_ptr<CKeyboardAcceleratorCollection> strongRef = weakCollection.first.lock () ;
			CKeyboardAcceleratorCollection* collection = strongRef;
			if (collection == null)
			{
				collectionsToGC.m_vector.emplace_back(weakCollection);
				continue;
			}

			DependencyObject parent = collection.GetParentInternal(false /* publicParentOnly */);
			while (parent != null)
			{
				if (parent.IsActive())
				{
					break;
				}
				parent = parent.GetParentInternal(false /* publicParentOnly */);
			}

			for (DependencyObject accelerator : *collection)
			{
				MUX_ASSERT(accelerator.OfTypeByIndex<KnownTypeIndex.KeyboardAccelerator>());

				if (policyFn((KeyboardAccelerator)(accelerator), owner) &&
					ShouldRaiseAcceleratorEvent(originalKey, keyModifiers, (KeyboardAccelerator)(accelerator), parent))
				{
					// The parent of the collection is the element that the collection belongs to.
					DependencyObject acceleratorParentElement = collection.GetParentInternal(false /* publicParentOnly */);

					UIElement* acceleratorUIElement = do_pointer_cast<UIElement>(acceleratorParentElement);
					// If the parent is disabled search for the next accelerator with enabled parent
					if (acceleratorUIElement && !acceleratorUIElement.IsEnabled)
					{
						continue;
					}
					// Now  it's time to check if accelerator is locally scoped accelerator,
					// in which case it will be skipped.
					KeyboardAccelerator pKAccelerator = (KeyboardAccelerator)(accelerator);
					if (isCallFromTryInvoke && IsAcceleratorLocallyScoped(pFocusedElement, pKAccelerator))
					{
						continue; // check for another accelerator.
					}

					// We found an accelerator to try invoking - even if it wasn't handled, we don't need to look any further
					return RaiseKeyboardAcceleratorInvoked(pKAccelerator, acceleratorParentElement);
				}
			}
		}

		// Clean up zombie KA collections
		allLiveAccelerators.erase(
			std.remove_if(
				allLiveAccelerators.begin(),
				allLiveAccelerators.end(),
				[&collectionsToGC](KACollectionAndRefCountPair & weakCollection)


				{
			var it = std.find(collectionsToGC.m_vector.begin(), collectionsToGC.m_vector.end(), weakCollection);
			return it != collectionsToGC.m_vector.end();
		}
            ),
        allLiveAccelerators.end()
	        );

		return false;
	}

	internal static void ProcessKeyboardAccelerators(
		  VirtualKey originalKey,
		  VirtualKeyModifiers keyModifiers,
		 VectorOfKACollectionAndRefCountPair& allLiveAccelerators,
		 DependencyObject pElement,
		out bool* pHandled,
		out bool* pHandledShouldNotImpedeTextInput,
		  DependencyObject pFocusedElement,
		 bool isCallFromTryInvoke)
	{
		// The order of things should be as follows:
		// 1) Try to process local accelerators defined on this element
		// 2) Try to process accelerators that are 'owned' by this element (KeyboardAccelerator.ScopeOwner==this)
		// 3) Give the element a chance to handle its own accelerators or forward on to attached ui (like a flyout) by calling the
		//    OnProcessKeyboardAccelerators protected virtual.
		// 4) Raise the public ProcessKeyboardAccelerators event
		//
		// If any of these gets handled, we don't need to do the rest - we just mark this as handled and return.  The caller should
		// copy the handled flag into the KeyRoutedEventArgs' handled property.

		*pHandled = false;
		*pHandledShouldNotImpedeTextInput = false;

		if (ProcessLocalAccelerators(originalKey, keyModifiers, pElement, pFocusedElement, isCallFromTryInvoke) ||
			ProcessOwnedAccelerators(originalKey, keyModifiers, allLiveAccelerators, pElement, pFocusedElement, isCallFromTryInvoke))
		{
			*pHandled = true;
		}
		else
		{
			UIElement* pElementAsUIE = do_pointer_cast<UIElement>(pElement);
			if (pElementAsUIE)
			{
				// Here, we try calling the OnProcessKeyboardAccelerators protected virtual,
				// after which we raise the public ProcessKeyboardAccelerators event
				(FxCallbacks.UIElement_RaiseProcessKeyboardAccelerators(
					pElementAsUIE,
					originalKey,
					keyModifiers,
					pHandled,
					pHandledShouldNotImpedeTextInput));
			}
		}
		return S_OK;
	}

	internal static bool ProcessLocalAccelerators(
		 VirtualKey originalKey,
		 VirtualKeyModifiers keyModifiers,
		 DependencyObject pElement,
		  DependencyObject pFocusedElement,
		 bool isCallFromTryInvoke)
	{
		// If the element is disabled, none of its accelerators are considered invocable anyway, so we can bail out early in that case
		UIElement* pUIElement = do_pointer_cast<UIElement>(pElement);
		if (pUIElement && !pUIElement.IsEnabled)
		{
			return false;
		}

		if (false == pElement.IsEffectiveValueInSparseStorage(KnownPropertyIndex.UIElement_KeyboardAccelerators))
		{
			return false;
		}

		CValue acceleratorCollectionValue;
		HRESULT hr = 0;
		/*VERIFYHR*/
		(hr = pElement.GetValueByIndex(KnownPropertyIndex.UIElement_KeyboardAccelerators, &acceleratorCollectionValue));
		if (/*FAILED*/(hr))
		{
			return false;
		}

		xref_ptr<DependencyObject> value = acceleratorCollectionValue.DetachObject();
		if (value == null)
		{
			return false;
		}

		CKeyboardAcceleratorCollection* pCollection = do_pointer_cast<CKeyboardAcceleratorCollection>(value);
		DependencyObject collectionParent = pCollection.GetParentInternal(false /*public parent only*/);
		for (DependencyObject accelerator : *pCollection)
		{
			if (ShouldRaiseAcceleratorEvent(originalKey, keyModifiers, (KeyboardAccelerator)(accelerator), collectionParent))
			{
				// Now  it's time to check if accelerator is not locally scoped accelerator.
				// in which case it will be skipped.
				if (isCallFromTryInvoke && IsAcceleratorLocallyScoped(pFocusedElement, (KeyboardAccelerator)(accelerator)))
				{
					continue; // check for another accelerator.
				}

				// We found an accelerator to try invoking - even if it wasn't handled, we don't need to look any further
				return RaiseKeyboardAcceleratorInvoked((KeyboardAccelerator)(accelerator), collectionParent);
			}
		}

		return false;
	}

	internal static bool AcceleratorIsOwnedPolicy(
		  KeyboardAccelerator accelerator,
		  DependencyObject owner)
	{
		// no need to proceed if accelerator we are processing is global.
		if (accelerator.m_scopeOwner == null)
		{
			return false;
		}
		//Below code will unwrap the managed object pointer for scopeOwner and returns actual pointer to compate with Owner.
		CValue value;
		/*VERIFYHR*/
		(accelerator.GetValueByIndex(KnownPropertyIndex.KeyboardAccelerator_ScopeOwner, &value));

		DependencyObject pDO = null;
		/*VERIFYHR*/
		(CValueBoxer.UnwrapWeakRef(&value, DirectUI.MetadataAPI.GetDependencyPropertyByIndex(KnownPropertyIndex.KeyboardAccelerator_ScopeOwner), &pDO));

		return pDO == owner;
	}

	internal static bool ProcessOwnedAccelerators(
		 VirtualKey originalKey,
		 VirtualKeyModifiers keyModifiers,
		 VectorOfKACollectionAndRefCountPair& allLiveAccelerators,
		 DependencyObject pElement,
		  DependencyObject pFocusedElement,
		 bool isCallFromTryInvoke)
	{
		return ProcessAllLiveAccelerators(originalKey, keyModifiers, allLiveAccelerators, pElement, AcceleratorIsOwnedPolicy, pFocusedElement, isCallFromTryInvoke);
	}

	internal static bool AcceleratorIsGlobalPolicy(
		  KeyboardAccelerator accelerator,
		  DependencyObject owner)
	{
		return accelerator.m_scopeOwner == null;
	}

	internal static bool ProcessGlobalAccelerators(
		 VirtualKey originalKey,
		 VirtualKeyModifiers keyModifiers,
		 VectorOfKACollectionAndRefCountPair& allLiveAccelerators)
	{
		return ProcessAllLiveAccelerators(originalKey, keyModifiers, allLiveAccelerators, null, AcceleratorIsGlobalPolicy, null, false);
	}

	internal static bool IsKeyValidForAccelerators(VirtualKey originalKey, uint modifierKeys)
	{
		bool isAlphaNumeric = (VirtualKey.Number0 <= originalKey) && (originalKey <= VirtualKey.Z);
		bool isNumpadKey = (VirtualKey.NumberPad0 <= originalKey) && (originalKey <= VirtualKey.Divide);
		bool isFunctionKey = (VirtualKey.F1 <= originalKey) && (originalKey <= VirtualKey.F24);

		//The following VK Codes are in winuser.w but not in Windows.System.VirtualKey
		//const int VK_BROWSER_FAVORITES = ;0xAB
		//const int VK_BROWSER_HOME = ;0xAC
		//const int VK_VOLUME_MUTE = ;0xAD
		//const int VK_VOLUME_DOWN = ;0xAE
		//const int VK_VOLUME_UP = ;0xAF
		//const int VK_MEDIA_NEXT_TRACK = ;0xB0
		//const int VK_MEDIA_PREV_TRACK = ;0xB1
		//const int VK_MEDIA_STOP = ;0xB2
		//const int VK_MEDIA_PLAY_PAUSE = ;0xB3
		//const int VK_LAUNCH_MAIL = ;0xB4
		//const int VK_LAUNCH_MEDIA_SELECT = ;0xB5
		//const int VK_LAUNCH_APP1 = ;0xB6
		//const int VK_LAUNCH_APP2 = ;0xB7
		//const int VK_APPCOMMAND_LAST = ;0xB7
		//const int VK_OEM_1 = ;0xBA   // ';:' for US
		//const int VK_OEM_PLUS = ;0xBB   // '+' any country/region
		//const int VK_OEM_COMMA = ;0xBC   // ',' any country/region
		//const int VK_OEM_MINUS = ;0xBD   // '-' any country/region
		//const int VK_OEM_PERIOD = ;0xBE   // '.' any country/region
		//const int VK_OEM_2 = ;0xBF   // '/?' for US
		//const int VK_OEM_3 = ;0xC0   // '`~' for US

		// We only want the 'symbol' keys from the extended VK set to be valid keyboard accelerators.
		bool isVKOEMSymbolKey = VK_OEM_1 <= originalKey && originalKey < VK_OEM_3;

		//Valid non symbol / aplha-numeric accesskeys are Enter, Esc, Backspace, Space, PageUp, PageDown, End, Home, Left, Up, Right,
		//Down, Snapshot, Insert, and Delete
		bool isValidNonSymbolAccessKey = (originalKey == VirtualKey.Enter)
						   || (originalKey == VirtualKey.Escape)
						   || (originalKey == VirtualKey.Back)
						   || ((modifierKeys & KEY_MODIFIER_CTRL) && (originalKey == VirtualKey.Tab))
						   || ((VirtualKey.Space <= originalKey) && (originalKey <= VirtualKey.Down))
						   || ((originalKey >= VirtualKey.Snapshot) && (originalKey <= VirtualKey.Delete));

		return isAlphaNumeric || isNumpadKey || isFunctionKey || isVKOEMSymbolKey || isValidNonSymbolAccessKey;
	}

	internal static bool TextInputHasPriorityForKey(VirtualKey originalKey, bool isCtrlPressed, bool isAltPressed)
	{
		if (isCtrlPressed || isAltPressed)
		{
			return false;
		}

		if ((VirtualKey.F1 <= originalKey) && (originalKey <= VirtualKey.F24))
		{
			return false;
		}

		if (originalKey == VirtualKey.Escape || originalKey == VirtualKey.Snapshot)
		{
			return false;
		}

		return true;
	}

	internal static bool ShouldRaiseAcceleratorEvent(
		 VirtualKey key,
		 VirtualKeyModifiers keyModifiers,
		  KeyboardAccelerator pAccelerator,
		 DependencyObject pParent)
	{
		return pAccelerator.m_isEnabled &&
			key == (VirtualKey)(pAccelerator.m_key) &&
			keyModifiers == (VirtualKeyModifiers)(pAccelerator.m_keyModifiers) &&
			FocusProperties.IsVisible(pParent) &&
			FocusProperties.AreAllAncestorsVisible(pParent);
	}

	// enum class VirtualKeyModifiers defined in windows.system.input.h and integer modifiers defined in paltypes.h are different in values
	// for Control and Alt. They are swapped as shown below.
	//                                                      VS
	// VirtualKeyModifiers.None     = 0                     |
	// VirtualKeyModifiers.Control  = 0x1                   |           const int KEY_MODIFIER_ALT = ;0x0001
	// VirtualKeyModifiers.Menu     = 0x2                   |           const int KEY_MODIFIER_CTRL = ;0x0002
	// VirtualKeyModifiers.Shift    = 0x4                   |           const int KEY_MODIFIER_SHIFT = ;0x0004
	// VirtualKeyModifiers.Windows  = 0x8                   |           const int KEY_MODIFIER_WINDOWS = ;0x0008
	//
	// This function maps VirtualKeyModifiers.Control and VirtualKeyModifiers.Menu to corresponding integer values
	internal static uint MapVirtualKeyModifiersToIntegersModifiers(VirtualKeyModifiers virtualKeyModifiers)
	{
		uint keyModifiers = 0;
		if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Control))
		{
			keyModifiers |= KEY_MODIFIER_CTRL;
		}
		if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Menu))
		{
			keyModifiers = KEY_MODIFIER_ALT;
		}
		if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
		{
			keyModifiers |= KEY_MODIFIER_SHIFT;
		}
		if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Windows))
		{
			keyModifiers |= KEY_MODIFIER_WINDOWS;
		}

		return keyModifiers;
	}
}

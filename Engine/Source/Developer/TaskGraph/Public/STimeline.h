// Copyright 1998-2014 Epic Games, Inc. All Rights Reserved.

#pragma once

/** A timeline widget.*/
class TASKGRAPH_API STimeline : public SCompoundWidget
{

public:
	SLATE_BEGIN_ARGS( STimeline )
		: _MinValue( 0.0f )
		, _MaxValue( 1.0f )
		{}

		/** Minimum value on the timeline */
		SLATE_ARGUMENT( float, MinValue )

		/** Maximum value on the timeline */
		SLATE_ARGUMENT( float, MaxValue )

		/** fixed pixel spacing between centers of labels */
		SLATE_ARGUMENT( float, FixedLabelSpacing )

	SLATE_END_ARGS()

	/**
	 * Construct the widget
	 * 
	 * @param InArgs   A declaration from which to construct the widget
	 */
	void Construct( const FArguments& InArgs );

	// SWidget interface
	virtual int32 OnPaint( const FPaintArgs& Args,  const FGeometry& AllottedGeometry, const FSlateRect& MyClippingRect, FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled ) const override;
	virtual void Tick( const FGeometry& AllottedGeometry, const double InCurrentTime, const float InDeltaTime ) override;
	virtual FReply OnMouseButtonDown( const FGeometry& MyGeometry, const FPointerEvent& MouseEvent ) override;
	virtual FReply OnMouseMove( const FGeometry& MyGeometry, const FPointerEvent& MouseEvent ) override;
	FVector2D ComputeDesiredSize() const override;
	// End of SWidget interface

	/**
	 * Sets the graph's zoom level
	 *
	 * @param NewValue	Zoom value, minimum is 1.0
	 */
	void SetZoom( float InZoom )
	{
		Zoom = FMath::Max(InZoom, 1.0f);
	}

	float GetZoom()
	{
		return Zoom;
	}
	/**
	 * Sets the graph's offset by which all graph bars should be moved
	 *
	 * @param NewValue	Offset value
	 */
	void SetOffset( float InOffset )
	{
		Offset = InOffset;
	} 

	/**
	 * Gets the graph's offset value
	 *
	 * @return Offset value
	 */
	float GetOffset() const
	{
		return Offset;
	}

	/**
	 * Sets the graph's min and max values.
	 *
	 * @param InMin	New min value.
	 * @param InMin	New max value.
	 */
	void SetMinMaxValues(float InMin, float InMax)
	{
		MinValue = InMin;
		MaxValue = InMax;
	}

	void GetMinMaxValues(float &InMin, float &InMax)
	{
		InMin = MinValue;
		InMax = MaxValue;
	}

	void SetDrawingGeometry(const FGeometry& Geometry)
	{
		DrawingGeometry = Geometry;
	}

	FGeometry GetDrawingGeometry() const
	{
		return DrawingGeometry;
	}

private:

	/** Background image to use for the graph bar */
	const FSlateBrush* BackgroundImage;

	/** Minimum value on the timeline */
	float MinValue;

	/** Maximum value on the timeline */
	float MaxValue;

	/** fixed pixel spacing between centers of labels */
	float FixedLabelSpacing;

	/** Current zoom of the graph */
	float Zoom;

	/** Current offset of the graph */
	float Offset;

	float DrawingOffsetX;

	FGeometry DrawingGeometry;
};
